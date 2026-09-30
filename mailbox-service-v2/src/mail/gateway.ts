import { randomUUID } from "node:crypto";
import { ImapFlow } from "imapflow";
import nodemailer from "nodemailer";
import type { Database } from "../db/database.js";
import type { Config } from "../config.js";
import type { MailCredential } from "../security/secrets.js";
import { resolveAuth } from "./oauth.js";

export interface WorkerAccount extends Record<string, unknown> {
  id: string; address: string; provider: string; imap_host: string; imap_port: number; imap_secure: boolean;
  smtp_host: string; smtp_port: number; smtp_secure: boolean; credential: MailCredential;
}

export class MailGateway {
  constructor(private readonly db: Database, private readonly config: Config) {}

  private async imap(account: WorkerAccount) {
    const auth = await resolveAuth(account.credential);
    return new ImapFlow({ host: account.imap_host, port: account.imap_port, secure: account.imap_secure,
      auth, logger: false, connectionTimeout: 30_000, greetingTimeout: 30_000, socketTimeout: this.config.taskTimeoutMs });
  }

  async validate(account: WorkerAccount) {
    let receive = false;
    let send = false;
    const client = await this.imap(account);
    try { await client.connect(); receive = true; } finally { await client.logout().catch(() => undefined); }
    const auth = await resolveAuth(account.credential);
    const transport = nodemailer.createTransport({ host: account.smtp_host, port: account.smtp_port, secure: account.smtp_secure, auth,
      connectionTimeout: 30_000, greetingTimeout: 30_000, socketTimeout: this.config.taskTimeoutMs });
    try { await transport.verify(); send = true; } finally { transport.close(); }
    await this.db.query(
      `UPDATE mail_accounts SET capability_receive=$2,capability_send=$3,status=CASE WHEN $2 AND $3 THEN 'ready' ELSE 'degraded' END,
       last_error_code=NULL,last_error_summary=NULL,updated_at=now() WHERE id=$1`, [account.id, receive, send]);
  }

  async sync(account: WorkerAccount) {
    const client = await this.imap(account);
    try {
      await client.connect();
      const lock = await client.getMailboxLock("INBOX");
      try {
        const since = new Date(Date.now() - this.config.syncLookbackDays * 86_400_000);
        const found = await client.search({ since }, { uid: true });
        const uids = Array.isArray(found) ? found.slice(-this.config.maxMessagesPerAccount) : [];
        for await (const item of client.fetch(uids, { uid: true, envelope: true, bodyStructure: true, size: true }, { uid: true })) {
          const envelope = item.envelope;
          if (!envelope) continue;
          const messageId = randomUUID();
          const recipients = [...(envelope.to ?? []), ...(envelope.cc ?? [])].map((v) => ({ name: v.name ?? "", address: v.address ?? "" }));
          await this.db.transaction(async (tx) => {
            await tx.query(
              `INSERT INTO messages(id,account_id,provider_uid,message_id,sender_name,sender_address,recipients,subject,text_body,html_body,received_at,has_attachments,size_bytes)
               VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9,$10,$11,$12,$13)
               ON CONFLICT(account_id,folder,provider_uid) DO UPDATE SET subject=EXCLUDED.subject,
                 received_at=EXCLUDED.received_at,has_attachments=EXCLUDED.has_attachments,size_bytes=EXCLUDED.size_bytes`,
              [messageId, account.id, String(item.uid), envelope.messageId ?? null, envelope.from?.[0]?.name ?? "",
                envelope.from?.[0]?.address ?? "", JSON.stringify(recipients), envelope.subject ?? "", "", null,
                envelope.date ?? new Date(), hasAttachments(item.bodyStructure), item.size ?? 0]);
          });
        }
        await this.db.query(
          `DELETE FROM messages WHERE account_id=$1 AND id NOT IN
           (SELECT id FROM messages WHERE account_id=$1 ORDER BY received_at DESC LIMIT $2)`, [account.id, this.config.maxMessagesPerAccount]);
        await this.db.query("UPDATE mail_accounts SET last_synced_at=now(),status='ready',capability_receive=true,last_error_code=NULL,last_error_summary=NULL,updated_at=now() WHERE id=$1", [account.id]);
      } finally { lock.release(); }
    } finally { await client.logout().catch(() => undefined); }
  }

  async fetchContent(account: WorkerAccount, payload: Record<string, unknown>) {
    const messageId = String(payload.messageId ?? "");
    const record = await this.db.query<{ provider_uid: string }>("SELECT provider_uid FROM messages WHERE id=$1 AND account_id=$2", [messageId, account.id]);
    const uid = Number(record.rows[0]?.provider_uid);
    if (!Number.isSafeInteger(uid) || uid < 1) throw new Error("Message not found");
    const client = await this.imap(account);
    try {
      await client.connect();
      const lock = await client.getMailboxLock("INBOX");
      try {
        const item = await client.fetchOne(uid, { source: true }, { uid: true });
        if (!item || !item.source) throw new Error("Message content unavailable");
        if (item.source.length > 25 * 1024 * 1024) throw new Error("Message exceeds the 25 MB on-demand limit");
        const { simpleParser } = await import("mailparser");
        const { default: sanitizeHtml } = await import("sanitize-html");
        const parsed = await simpleParser(item.source, { skipHtmlToText: false, skipTextToHtml: true });
        const html = typeof parsed.html === "string" ? sanitizeHtml(parsed.html, {
          allowedTags: sanitizeHtml.defaults.allowedTags.filter((tag) => tag !== "img"),
          allowedAttributes: { a: ["href", "title"], blockquote: ["cite"] }, allowedSchemes: ["http", "https", "mailto"]
        }) : null;
        await this.db.transaction(async (tx) => {
          await tx.query("UPDATE messages SET text_body=$2,html_body=$3,has_attachments=$4 WHERE id=$1", [messageId, parsed.text ?? "", html, parsed.attachments.length > 0]);
          for (const attachment of parsed.attachments) {
            await tx.query(
              `INSERT INTO attachments(id,message_id,provider_part_id,filename,content_type,size_bytes,content,fetched_at)
               VALUES($1,$2,$3,$4,$5,$6,$7,now()) ON CONFLICT(message_id,provider_part_id) DO UPDATE SET content=EXCLUDED.content,fetched_at=now()`,
              [randomUUID(), messageId, attachment.contentId ?? attachment.checksum ?? randomUUID(), attachment.filename ?? "attachment",
                attachment.contentType, attachment.size, attachment.content]);
          }
        });
      } finally { lock.release(); }
    } finally { await client.logout().catch(() => undefined); }
  }

  async send(account: WorkerAccount, payload: Record<string, unknown>) {
    const to = Array.isArray(payload.to) ? payload.to.map(String) : [];
    if (to.length === 0 || to.length > 50) throw new Error("Send task requires 1-50 recipients");
    const auth = await resolveAuth(account.credential);
    const transport = nodemailer.createTransport({ host: account.smtp_host, port: account.smtp_port, secure: account.smtp_secure, auth,
      connectionTimeout: 30_000, greetingTimeout: 30_000, socketTimeout: this.config.taskTimeoutMs });
    try {
      await transport.sendMail({ from: account.address, to, subject: String(payload.subject ?? "").slice(0, 998),
        text: String(payload.text ?? "").slice(0, 1_000_000) });
      await this.db.query("UPDATE mail_accounts SET capability_send=true,status=CASE WHEN capability_receive THEN 'ready' ELSE status END,updated_at=now() WHERE id=$1", [account.id]);
    } finally { transport.close(); }
  }
}

function hasAttachments(node: unknown): boolean {
  if (!node || typeof node !== "object") return false;
  const value = node as { disposition?: string; childNodes?: unknown[] };
  return value.disposition?.toLowerCase() === "attachment" || (value.childNodes?.some(hasAttachments) ?? false);
}
