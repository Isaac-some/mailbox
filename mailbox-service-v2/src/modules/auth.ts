import { randomBytes, randomUUID } from "node:crypto";
import type { Database } from "../db/database.js";
import { hashOpaque, hashPassword, verifyPassword } from "../security/secrets.js";
import { AuditLog } from "./audit.js";

export interface Actor { id: string; username: string; role: "admin" | "member"; csrfToken: string; }

export class AuthModule {
  constructor(private readonly db: Database, private readonly sessionKey: Buffer, private readonly audit: AuditLog) {}

  async seedAdmin(username: string, password: string) {
    const count = await this.db.query<{ count: string }>("SELECT count(*)::text AS count FROM users");
    if (count.rows[0]?.count !== "0") return false;
    await this.db.query("INSERT INTO users(id,username,password_hash,role) VALUES($1,$2,$3,'admin')",
      [randomUUID(), username, await hashPassword(password)]);
    return true;
  }

  async canAttemptLogin(keyHash: string) {
    const result = await this.db.query<{ blocked_until: string | Date | null }>("SELECT blocked_until FROM auth_throttles WHERE key_hash=$1", [keyHash]);
    const blocked = result.rows[0]?.blocked_until;
    return !blocked || new Date(blocked).getTime() <= Date.now();
  }

  async noteLoginAttempt(keyHash: string, succeeded: boolean) {
    if (succeeded) {
      await this.db.query("DELETE FROM auth_throttles WHERE key_hash=$1", [keyHash]);
      return;
    }
    await this.db.query(
      `INSERT INTO auth_throttles(key_hash,failures) VALUES($1,1)
       ON CONFLICT(key_hash) DO UPDATE SET
         failures=CASE WHEN auth_throttles.window_started_at < now()-interval '15 minutes' THEN 1 ELSE auth_throttles.failures+1 END,
         window_started_at=CASE WHEN auth_throttles.window_started_at < now()-interval '15 minutes' THEN now() ELSE auth_throttles.window_started_at END,
         blocked_until=CASE WHEN auth_throttles.failures+1 >= 5 THEN now()+interval '15 minutes' ELSE auth_throttles.blocked_until END`, [keyHash]);
  }

  async login(username: string, password: string, ipHash?: string, userAgent?: string) {
    const result = await this.db.query<{ id: string; username: string; password_hash: string; role: "admin" | "member"; is_active: boolean }>(
      "SELECT id,username,password_hash,role,is_active FROM users WHERE username=$1", [username.trim().toLowerCase()]);
    const user = result.rows[0];
    if (!user || !user.is_active || !await verifyPassword(password, user.password_hash)) {
      await this.audit.record({ action: "auth.login", targetType: "user", ...(user?.id ? { targetId: user.id } : {}), outcome: "denied", ...(ipHash ? { ipHash } : {}) });
      return null;
    }
    const token = randomBytes(32).toString("base64url");
    const csrfToken = randomBytes(24).toString("base64url");
    await this.db.transaction(async (tx) => {
      await tx.query(
        `INSERT INTO sessions(id_hash,user_id,csrf_token,expires_at,ip_hash,user_agent)
         VALUES($1,$2,$3,now()+interval '12 hours',$4,$5)`,
        [hashOpaque(token, this.sessionKey), user.id, csrfToken, ipHash ?? null, userAgent?.slice(0, 300) ?? null]);
      await this.audit.record({ actorUserId: user.id, action: "auth.login", targetType: "user", targetId: user.id, outcome: "success", ...(ipHash ? { ipHash } : {}) }, tx);
    });
    return { token, actor: { id: user.id, username: user.username, role: user.role, csrfToken } satisfies Actor };
  }

  async actor(token: string | undefined): Promise<Actor | null> {
    if (!token) return null;
    const result = await this.db.query<Actor & { csrf_token: string } & Record<string, unknown>>(
      `SELECT u.id,u.username,u.role,s.csrf_token FROM sessions s JOIN users u ON u.id=s.user_id
       WHERE s.id_hash=$1 AND s.expires_at>now() AND u.is_active=true`, [hashOpaque(token, this.sessionKey)]);
    const row = result.rows[0];
    if (!row) return null;
    await this.db.query("UPDATE sessions SET last_seen_at=now() WHERE id_hash=$1 AND last_seen_at < now()-interval '5 minutes'", [hashOpaque(token, this.sessionKey)]);
    return { id: row.id, username: row.username, role: row.role, csrfToken: row.csrf_token };
  }

  async logout(token: string, actorUserId: string) {
    await this.db.query("DELETE FROM sessions WHERE id_hash=$1", [hashOpaque(token, this.sessionKey)]);
    await this.audit.record({ actorUserId, action: "auth.logout", targetType: "user", targetId: actorUserId, outcome: "success" });
  }

  async createUser(username: string, password: string, role: "admin" | "member", actorUserId: string) {
    const existing = await this.db.query<{ count: string }>("SELECT count(*)::text AS count FROM users WHERE is_active=true");
    if (Number(existing.rows[0]?.count ?? 0) >= 10) throw new Error("USER_LIMIT_REACHED");
    const id = randomUUID();
    await this.db.transaction(async (tx) => {
      await tx.query("INSERT INTO users(id,username,password_hash,role) VALUES($1,$2,$3,$4)",
        [id, username.trim().toLowerCase(), await hashPassword(password), role]);
      await this.audit.record({ actorUserId, action: "user.create", targetType: "user", targetId: id, outcome: "success", details: { username, role } }, tx);
    });
    return { id, username, role };
  }

  async listUsers() {
    return (await this.db.query("SELECT id,username,role,is_active,created_at,last_seen_at FROM users u LEFT JOIN LATERAL (SELECT max(last_seen_at) last_seen_at FROM sessions s WHERE s.user_id=u.id) x ON true ORDER BY created_at")).rows;
  }
}
