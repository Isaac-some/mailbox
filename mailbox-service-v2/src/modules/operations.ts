import type { Database } from "../db/database.js";

export class OperationsModule {
  constructor(private readonly db: Database) {}

  async dashboard() {
    const [accounts, tasks, messages, failures] = await Promise.all([
      this.db.query<{ total: string; ready: string; degraded: string }>(
        "SELECT count(*)::text total,count(*) FILTER(WHERE status='ready')::text ready,count(*) FILTER(WHERE status='degraded')::text degraded FROM mail_accounts"),
      this.db.query<{ queued: string; running: string; failed: string }>(
        "SELECT count(*) FILTER(WHERE state='queued')::text queued,count(*) FILTER(WHERE state='running')::text running,count(*) FILTER(WHERE state='failed')::text failed FROM mail_tasks"),
      this.db.query<{ total: string }>("SELECT count(*)::text total FROM messages"),
      this.db.query("SELECT provider,last_failure_code,consecutive_failures,paused_until,next_connect_at FROM provider_gates ORDER BY provider")
    ]);
    return { accounts: accounts.rows[0], tasks: tasks.rows[0], messages: messages.rows[0], providerGates: failures.rows };
  }

  async audit(limit = 200) {
    return (await this.db.query(
      `SELECT e.id,e.action,e.target_type,e.target_id,e.outcome,e.details,e.created_at,u.username
       FROM audit_events e LEFT JOIN users u ON u.id=e.actor_user_id ORDER BY e.created_at DESC LIMIT $1`, [limit])).rows;
  }
}

