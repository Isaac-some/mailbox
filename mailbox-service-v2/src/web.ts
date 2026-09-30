import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";
import cookie from "@fastify/cookie";
import fastifyStatic from "@fastify/static";
import Fastify, { type FastifyRequest } from "fastify";
import { parse } from "csv-parse/sync";
import { stringify } from "csv-stringify/sync";
import { z } from "zod";
import type { AppContext } from "./context.js";
import { accountInputSchema } from "./domain/providers.js";
import { hashOpaque } from "./security/secrets.js";

declare module "fastify" {
  interface FastifyRequest { actor: import("./modules/auth.js").Actor | null; }
}

const credentialsSchema = accountInputSchema;
const sendSchema = z.object({ to: z.array(z.string().email()).min(1).max(50), subject: z.string().max(998), text: z.string().max(1_000_000) });

export async function buildWeb(context: AppContext) {
  const app = Fastify({ logger: { redact: ["req.headers.authorization", "req.headers.cookie", "req.body.password", "req.body.credential", "res.headers.set-cookie"] },
    trustProxy: context.config.trustProxy, bodyLimit: 2 * 1024 * 1024 });
  await app.register(cookie);
  await app.register(fastifyStatic, { root: fileURLToPath(new URL("./public", import.meta.url)), prefix: "/" });

  app.decorateRequest("actor", null);
  app.addHook("onRequest", async (request, reply) => {
    request.actor = await context.auth.actor(request.cookies.mailbox_session);
    const pathname = request.url.split("?")[0];
    if (!pathname?.startsWith("/api/") || pathname === "/api/login" || pathname === "/api/session" || pathname.startsWith("/health/")) return;
    if (!request.actor) return reply.code(401).send({ error: "请先登录" });
    if (!["GET", "HEAD", "OPTIONS"].includes(request.method)) {
      if (request.headers.origin !== context.config.publicOrigin || request.headers["x-csrf-token"] !== request.actor.csrfToken) {
        await context.audit.record({ actorUserId: request.actor.id, action: "request.csrf", targetType: "http_request", outcome: "denied",
          details: { method: request.method, path: pathname } });
        return reply.code(403).send({ error: "请求校验失败，请刷新页面后重试" });
      }
    }
  });

  app.setErrorHandler((rawError, request, reply) => {
    const error = rawError as Error & { issues?: Array<{ message: string }>; statusCode?: number };
    request.log.error({ err: error }, "request failed");
    const status = rawError instanceof z.ZodError ? 400 : error.message.includes("duplicate key") ? 409 : error.statusCode ?? 500;
    const message = status === 400 ? (error.issues ?? []).map((issue) => issue.message).join("；") : status === 409 ? "记录已存在" : "操作失败，请查看任务或审计记录";
    reply.code(status).send({ error: message });
  });

  app.get("/health/live", async () => ({ status: "ok" }));
  app.get("/health/ready", async (_request, reply) => {
    await context.db.query("SELECT 1");
    return reply.send({ status: "ready" });
  });

  app.post("/api/login", async (request, reply) => {
    const body = z.object({ username: z.string().min(1).max(100), password: z.string().min(1).max(4096) }).parse(request.body);
    const ipHash = requestIdentity(request, context.sessionKey);
    const throttleKey = hashOpaque(`${ipHash}:${body.username.toLowerCase()}`, context.sessionKey);
    if (!await context.auth.canAttemptLogin(throttleKey)) return reply.code(429).send({ error: "登录失败次数过多，请 15 分钟后重试" });
    const result = await context.auth.login(body.username, body.password, ipHash, request.headers["user-agent"]);
    await context.auth.noteLoginAttempt(throttleKey, Boolean(result));
    if (!result) return reply.code(401).send({ error: "用户名或密码错误" });
    reply.setCookie("mailbox_session", result.token, { httpOnly: true, secure: context.config.env === "production", sameSite: "strict", path: "/", maxAge: 12 * 60 * 60 });
    return { actor: result.actor };
  });

  app.get("/api/session", async (request) => ({ actor: request.actor }));
  app.post("/api/logout", async (request, reply) => {
    await context.auth.logout(request.cookies.mailbox_session!, request.actor!.id);
    reply.clearCookie("mailbox_session", { path: "/" });
    return { ok: true };
  });
  app.get("/api/dashboard", async () => context.operations.dashboard());

  app.get("/api/accounts", async (request) => {
    const query = z.object({ q: z.string().max(200).default(""), offset: z.coerce.number().int().min(0).default(0) }).parse(request.query);
    return { items: await context.accounts.list(query.q, 100, query.offset) };
  });
  app.post("/api/accounts", async (request) => {
    const account = await context.accounts.create(credentialsSchema.parse(request.body), request.actor!.id);
    const task = await context.jobs.enqueue(account.id, "validate", 100, request.actor!.id);
    return { account, task };
  });
  app.delete("/api/accounts/:id", async (request, reply) => {
    requireAdmin(request);
    const params = z.object({ id: z.string().uuid() }).parse(request.params);
    const body = z.object({ confirm: z.string().email() }).parse(request.body);
    const current = (await context.accounts.list(body.confirm, 2, 0)).filter((a) => a.id === params.id);
    if (current.length !== 1 || current[0]?.address !== body.confirm.toLowerCase()) return reply.code(400).send({ error: "确认邮箱地址不匹配" });
    await context.accounts.remove(params.id, request.actor!.id);
    return { ok: true };
  });
  app.post("/api/accounts/:id/tasks", async (request) => {
    const params = z.object({ id: z.string().uuid() }).parse(request.params);
    const body = z.object({ kind: z.enum(["validate", "sync"]) }).parse(request.body);
    return { task: await context.jobs.enqueue(params.id, body.kind, 100, request.actor!.id) };
  });
  app.post("/api/accounts/:id/send", async (request) => {
    const params = z.object({ id: z.string().uuid() }).parse(request.params);
    const body = sendSchema.parse(request.body);
    return { task: await context.jobs.enqueue(params.id, "send", 100, request.actor!.id, body) };
  });

  app.post("/api/accounts/import", async (request) => {
    const body = z.object({ csv: z.string().max(1_500_000) }).parse(request.body);
    const rows = parse(body.csv, { columns: true, skip_empty_lines: true, trim: true, bom: true }) as Record<string, string>[];
    if (rows.length > 10_000) throw new Error("Import exceeds 10,000 rows");
    const results: Array<Record<string, unknown>> = [];
    for (const row of rows) {
      try {
        const account = await context.accounts.create({ address: row.address, displayName: row.display_name ?? "", provider: row.provider,
          imapHost: row.imap_host || undefined, imapPort: row.imap_port || undefined, smtpHost: row.smtp_host || undefined,
          smtpPort: row.smtp_port || undefined, credential: { type: "password", username: row.username || row.address, secret: row.password } }, request.actor!.id);
        await context.jobs.enqueue(account.id, "validate", 0, request.actor!.id);
        results.push({ address: row.address, status: "queued" });
      } catch { results.push({ address: row.address, status: "rejected" }); }
    }
    await context.audit.record({ actorUserId: request.actor!.id, action: "account.import", targetType: "mail_account", outcome: "success",
      details: { rows: rows.length, queued: results.filter((r) => r.status === "queued").length } });
    return { results };
  });
  app.get("/api/accounts/export", async (_request, reply) => {
    const rows = await context.accounts.list("", 10_000, 0);
    const csv = stringify(rows.map((row) => ({ address: row.address, display_name: row.display_name, provider: row.provider,
      status: row.status, receive: row.capability_receive, send: row.capability_send, last_synced_at: row.last_synced_at })), { header: true });
    reply.header("content-type", "text/csv; charset=utf-8").header("content-disposition", "attachment; filename=mail-accounts.csv");
    return `\ufeff${csv}`;
  });

  app.get("/api/messages", async (request) => {
    const query = z.object({ q: z.string().max(200).default(""), accountId: z.string().default(""), offset: z.coerce.number().int().min(0).default(0) }).parse(request.query);
    return { items: await context.messages.list(query.q, query.accountId, 100, query.offset) };
  });
  app.get("/api/messages/:id", async (request, reply) => {
    const { id } = z.object({ id: z.string().uuid() }).parse(request.params);
    const item = await context.messages.get(id) as ({ account_id: string } & Record<string, unknown>) | null;
    return item ? { item } : reply.code(404).send({ error: "邮件不存在" });
  });
  app.post("/api/messages/:id/content", async (request, reply) => {
    const { id } = z.object({ id: z.string().uuid() }).parse(request.params);
    const item = await context.messages.get(id) as ({ account_id: string } & Record<string, unknown>) | null;
    if (!item) return reply.code(404).send({ error: "邮件不存在" });
    return { task: await context.jobs.enqueue(String(item.account_id), "content", 100, request.actor!.id, { messageId: id }) };
  });
  app.get("/api/attachments/:id", async (request, reply) => {
    const { id } = z.object({ id: z.string().uuid() }).parse(request.params);
    const item = await context.messages.attachment(id);
    if (!item) return reply.code(404).send({ error: "附件尚未读取" });
    reply.header("content-type", item.content_type).header("content-disposition", `attachment; filename*=UTF-8''${encodeURIComponent(item.filename)}`)
      .header("x-content-type-options", "nosniff");
    return reply.send(Buffer.from(item.content));
  });
  app.get("/api/tasks", async () => ({ items: await context.jobs.list(200) }));
  app.get("/api/audit", async () => ({ items: await context.operations.audit(200) }));
  app.get("/api/users", async (request) => { requireAdmin(request); return { items: await context.auth.listUsers() }; });
  app.post("/api/users", async (request) => {
    requireAdmin(request);
    const body = z.object({ username: z.string().min(3).max(100), password: z.string().min(12).max(4096), role: z.enum(["admin", "member"]) }).parse(request.body);
    return { user: await context.auth.createUser(body.username, body.password, body.role, request.actor!.id) };
  });

  return app;
}

function requireAdmin(request: FastifyRequest) {
  if (request.actor?.role !== "admin") {
    const error = new Error("ADMIN_REQUIRED");
    Object.assign(error, { statusCode: 403 });
    throw error;
  }
}

function requestIdentity(request: FastifyRequest, key: Buffer) {
  return createHash("sha256").update(key).update(request.ip).digest("base64url");
}
