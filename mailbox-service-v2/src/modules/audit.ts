import { randomUUID } from "node:crypto";
import type { Database, Transaction } from "../db/database.js";

export type AuditOutcome = "success" | "denied" | "failure";

export class AuditLog {
  constructor(private readonly db: Database) {}

  async record(event: {
    actorUserId?: string; action: string; targetType: string; targetId?: string;
    outcome: AuditOutcome; details?: Record<string, unknown>; ipHash?: string;
  }, tx: Transaction = this.db) {
    await tx.query(
      `INSERT INTO audit_events(id, actor_user_id, action, target_type, target_id, outcome, details, ip_hash)
       VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8)`,
      [randomUUID(), event.actorUserId ?? null, event.action, event.targetType, event.targetId ?? null,
        event.outcome, JSON.stringify(event.details ?? {}), event.ipHash ?? null]
    );
  }
}

