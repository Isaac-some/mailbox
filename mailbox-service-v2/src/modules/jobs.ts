import { randomUUID } from "node:crypto";
import type { Database } from "../db/database.js";

export type TaskKind = "validate" | "sync" | "send" | "content";
export type TaskPriority = 0 | 100;

export interface MailTask {
  id: string; account_id: string; kind: TaskKind; priority: number; state: string;
  payload: Record<string, unknown>; attempts: number; max_attempts: number;
}

export class JobQueue {
  constructor(private readonly db: Database) {}

  async enqueue(accountId: string, kind: TaskKind, priority: TaskPriority, requestedBy: string, payload: Record<string, unknown> = {}) {
    const id = randomUUID();
    if (kind === "content") {
      const inserted = await this.db.query<{ id: string; state: string; priority: number }>(
        `INSERT INTO mail_tasks(id,account_id,kind,priority,payload,requested_by) VALUES ($1,$2,$3,$4,$5::jsonb,$6) RETURNING id,state,priority`,
        [id, accountId, kind, priority, JSON.stringify(payload), requestedBy]);
      return inserted.rows[0]!;
    }
    const result = await this.db.query<{ id: string; state: string; priority: number }>(
      `INSERT INTO mail_tasks(id,account_id,kind,priority,payload,requested_by)
       VALUES ($1,$2,$3,$4,$5::jsonb,$6)
       ON CONFLICT (account_id,kind) WHERE state IN ('queued','running') DO UPDATE
       SET priority=GREATEST(mail_tasks.priority,EXCLUDED.priority), updated_at=now()
       RETURNING id,state,priority`, [id, accountId, kind, priority, JSON.stringify(payload), requestedBy]);
    return result.rows[0]!;
  }

  async claim(workerId: string, leaseMs: number): Promise<MailTask | null> {
    return this.db.transaction(async (tx) => {
      await tx.query(
        `UPDATE mail_tasks SET state='queued',lease_owner=NULL,lease_expires_at=NULL,available_at=now(),updated_at=now(),
           last_error_code='LEASE_EXPIRED',last_error_summary='Worker lease expired; task recovered'
         WHERE state='running' AND lease_expires_at < now()`);
      const picked = await tx.query<MailTask>(
        `SELECT t.* FROM mail_tasks t
         WHERE t.state='queued' AND t.available_at <= now()
           AND NOT EXISTS (SELECT 1 FROM mail_tasks r WHERE r.account_id=t.account_id AND r.state='running')
         ORDER BY t.priority DESC,t.available_at,t.created_at
         FOR UPDATE SKIP LOCKED LIMIT 1`);
      const task = picked.rows[0];
      if (!task) return null;
      const claimed = await tx.query<MailTask>(
        `UPDATE mail_tasks SET state='running',attempts=attempts+1,lease_owner=$2,
           lease_expires_at=now()+($3 * interval '1 millisecond'),started_at=COALESCE(started_at,now()),updated_at=now()
         WHERE id=$1 RETURNING *`, [task.id, workerId, leaseMs]);
      return claimed.rows[0] ?? null;
    });
  }

  async renew(taskId: string, workerId: string, leaseMs: number) {
    const result = await this.db.query(
      `UPDATE mail_tasks SET lease_expires_at=now()+($3 * interval '1 millisecond'),updated_at=now()
       WHERE id=$1 AND lease_owner=$2 AND state='running'`, [taskId, workerId, leaseMs]);
    return result.rowCount === 1;
  }

  async defer(taskId: string, workerId: string, availableAt: Date) {
    await this.db.query(
      `UPDATE mail_tasks SET state='queued',available_at=$3,lease_owner=NULL,lease_expires_at=NULL,updated_at=now()
       WHERE id=$1 AND lease_owner=$2 AND state='running'`, [taskId, workerId, availableAt]);
  }

  async succeed(taskId: string, workerId: string) {
    await this.db.query(
      `UPDATE mail_tasks SET state='succeeded',finished_at=now(),lease_owner=NULL,lease_expires_at=NULL,updated_at=now()
       WHERE id=$1 AND lease_owner=$2 AND state='running'`, [taskId, workerId]);
  }

  async fail(task: MailTask, workerId: string, error: { code: string; summary: string }, retryDelayMs: number | null) {
    const retry = retryDelayMs !== null && task.attempts < task.max_attempts;
    await this.db.query(
      `UPDATE mail_tasks SET state=$3,last_error_code=$4,last_error_summary=$5,lease_owner=NULL,lease_expires_at=NULL,
         available_at=CASE WHEN $3='queued' THEN now()+($6 * interval '1 millisecond') ELSE available_at END,
         finished_at=CASE WHEN $3='failed' THEN now() ELSE NULL END,updated_at=now()
       WHERE id=$1 AND lease_owner=$2 AND state='running'`,
      [task.id, workerId, retry ? "queued" : "failed", error.code, error.summary, retryDelayMs ?? 0]);
  }

  async list(limit = 100) {
    return (await this.db.query(
      `SELECT t.id,t.kind,t.priority,t.state,t.attempts,t.max_attempts,t.available_at,t.last_error_code,t.last_error_summary,
         t.created_at,t.started_at,t.finished_at,a.address,a.provider
       FROM mail_tasks t JOIN mail_accounts a ON a.id=t.account_id ORDER BY t.created_at DESC LIMIT $1`, [limit])).rows;
  }
}
