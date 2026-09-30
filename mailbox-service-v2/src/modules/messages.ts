import type { Database } from "../db/database.js";

export class MessageModule {
  constructor(private readonly db: Database) {}

  async list(search = "", accountId = "", limit = 100, offset = 0) {
    const query = search.trim();
    return (await this.db.query(
      `SELECT m.id,m.account_id,m.sender_name,m.sender_address,m.subject,m.received_at,m.has_attachments,m.size_bytes,
         (m.text_body <> '' OR m.html_body IS NOT NULL) AS content_loaded,a.address
       FROM messages m JOIN mail_accounts a ON a.id=m.account_id
       WHERE ($1='' OR m.search_document @@ websearch_to_tsquery('simple',$1)) AND ($2='' OR m.account_id::text=$2)
       ORDER BY m.received_at DESC LIMIT $3 OFFSET $4`, [query, accountId, limit, offset])).rows;
  }

  async get(id: string) {
    const message = (await this.db.query(
      `SELECT m.id,m.account_id,m.sender_name,m.sender_address,m.recipients,m.subject,m.text_body,m.html_body,m.received_at,
         m.has_attachments,m.size_bytes,a.address FROM messages m JOIN mail_accounts a ON a.id=m.account_id WHERE m.id=$1`, [id])).rows[0];
    if (!message) return null;
    const attachments = (await this.db.query(
      "SELECT id,filename,content_type,size_bytes,(content IS NOT NULL) AS available FROM attachments WHERE message_id=$1 ORDER BY filename", [id])).rows;
    return { ...message, attachments };
  }

  async attachment(id: string) {
    return (await this.db.query<{ filename: string; content_type: string; content: Uint8Array }>(
      "SELECT filename,content_type,content FROM attachments WHERE id=$1 AND content IS NOT NULL", [id])).rows[0] ?? null;
  }
}

