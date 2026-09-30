import { randomUUID } from "node:crypto";
import type { Database } from "../db/database.js";
import { accountInputSchema, resolveEndpoints } from "../domain/providers.js";
import { CredentialVault } from "../security/secrets.js";
import { AuditLog } from "./audit.js";

export class AccountModule {
  constructor(private readonly db: Database, private readonly vault: CredentialVault, private readonly audit: AuditLog) {}

  async create(raw: unknown, actorUserId: string) {
    const input = accountInputSchema.parse(raw);
    const endpoint = resolveEndpoints(input);
    const credential = input.credential.type === "oauth_refresh" && input.credential.clientSecret === undefined
      ? { ...input.credential, clientSecret: undefined } as never
      : input.credential;
    const id = randomUUID();
    await this.db.transaction(async (tx) => {
      await tx.query(
        `INSERT INTO mail_accounts
          (id,address,display_name,provider,imap_host,imap_port,imap_secure,smtp_host,smtp_port,smtp_secure,credential_ciphertext,created_by)
         VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)`,
        [id, input.address, input.displayName, input.provider, endpoint.imapHost, endpoint.imapPort, endpoint.imapSecure,
          endpoint.smtpHost, endpoint.smtpPort, endpoint.smtpSecure, this.vault.encrypt(credential), actorUserId]
      );
      await this.audit.record({ actorUserId, action: "account.create", targetType: "mail_account", targetId: id,
        outcome: "success", details: { address: input.address, provider: input.provider } }, tx);
    });
    return { id, address: input.address };
  }

  async list(search = "", limit = 100, offset = 0) {
    const value = `%${search.replace(/[\\%_]/g, "\\$&")}%`;
    return (await this.db.query(
      `SELECT id,address,display_name,provider,status,capability_receive,capability_send,last_synced_at,last_error_code,last_error_summary,created_at
       FROM mail_accounts WHERE ($1 = '%%' OR address ILIKE $1 ESCAPE '\\' OR display_name ILIKE $1 ESCAPE '\\')
       ORDER BY updated_at DESC LIMIT $2 OFFSET $3`, [value, limit, offset])).rows;
  }

  async getForWorker(id: string) {
    const result = await this.db.query<Record<string, unknown>>(`SELECT * FROM mail_accounts WHERE id=$1 AND status <> 'disabled'`, [id]);
    const row = result.rows[0];
    if (!row) return null;
    return { ...row, credential: this.vault.decrypt(String(row.credential_ciphertext)), credential_ciphertext: undefined };
  }

  async remove(id: string, actorUserId: string) {
    await this.db.transaction(async (tx) => {
      const account = await tx.query<{ address: string }>("SELECT address FROM mail_accounts WHERE id=$1 FOR UPDATE", [id]);
      if (!account.rows[0]) throw new Error("ACCOUNT_NOT_FOUND");
      await tx.query("DELETE FROM mail_accounts WHERE id=$1", [id]);
      await this.audit.record({ actorUserId, action: "account.delete", targetType: "mail_account", targetId: id,
        outcome: "success", details: { address: account.rows[0].address } }, tx);
    });
  }
}
