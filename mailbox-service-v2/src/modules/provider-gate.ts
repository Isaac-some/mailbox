import type { Database } from "../db/database.js";
import type { Provider } from "../domain/providers.js";

export class ProviderGate {
  constructor(private readonly db: Database, private readonly intervalMs: number, private readonly pauseThreshold: number) {}

  async acquire(provider: Provider, leaseOwner: string, leaseMs: number): Promise<{ granted: true } | { granted: false; retryAt: Date }> {
    return this.db.transaction(async (tx) => {
      await tx.query("INSERT INTO provider_gates(provider) VALUES($1) ON CONFLICT(provider) DO NOTHING", [provider]);
      const result = await tx.query<{ next_connect_at: string | Date; paused_until: string | Date | null; active_lease_expires_at: string | Date | null }>(
        "SELECT next_connect_at,paused_until,active_lease_expires_at FROM provider_gates WHERE provider=$1 FOR UPDATE", [provider]);
      const row = result.rows[0]!;
      const now = Date.now();
      const retryAt = Math.max(new Date(row.next_connect_at).getTime(), row.paused_until ? new Date(row.paused_until).getTime() : 0,
        row.active_lease_expires_at ? new Date(row.active_lease_expires_at).getTime() : 0);
      if (retryAt > now) return { granted: false as const, retryAt: new Date(retryAt) };
      await tx.query(
        `UPDATE provider_gates SET next_connect_at=now()+($2 * interval '1 millisecond'),active_lease_owner=$3,
          active_lease_expires_at=now()+($4 * interval '1 millisecond'),updated_at=now() WHERE provider=$1`,
        [provider, this.intervalMs, leaseOwner, leaseMs]);
      return { granted: true as const };
    });
  }

  async release(provider: Provider, leaseOwner: string) {
    await this.db.query(
      "UPDATE provider_gates SET active_lease_owner=NULL,active_lease_expires_at=NULL,updated_at=now() WHERE provider=$1 AND active_lease_owner=$2",
      [provider, leaseOwner]);
  }

  async success(provider: Provider) {
    await this.db.query(
      "UPDATE provider_gates SET consecutive_failures=0,last_failure_code=NULL,paused_until=NULL,updated_at=now() WHERE provider=$1", [provider]);
  }

  async failure(provider: Provider, code: string, requestedPauseMs: number) {
    await this.db.query(
      `UPDATE provider_gates SET consecutive_failures=consecutive_failures+1,last_failure_code=$2,
         paused_until=CASE
           WHEN $3 > 0 OR consecutive_failures+1 >= $4
           THEN GREATEST(COALESCE(paused_until,now()),now()+((CASE WHEN $3 > 0 THEN $3 ELSE 60000 END) * interval '1 millisecond'))
           ELSE paused_until END,updated_at=now() WHERE provider=$1`,
      [provider, code, requestedPauseMs, this.pauseThreshold]);
  }

  async status() { return (await this.db.query("SELECT * FROM provider_gates ORDER BY provider")).rows; }
}
