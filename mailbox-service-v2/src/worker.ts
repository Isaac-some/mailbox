import { randomUUID } from "node:crypto";
import type { Config } from "./config.js";
import type { Database } from "./db/database.js";
import type { Provider } from "./domain/providers.js";
import { retryDecision } from "./domain/retry.js";
import { MailGateway, type WorkerAccount } from "./mail/gateway.js";
import { AccountModule } from "./modules/accounts.js";
import { JobQueue, type MailTask } from "./modules/jobs.js";
import { ProviderGate } from "./modules/provider-gate.js";
import { redactError } from "./security/secrets.js";

export async function runWorker(deps: {
  db: Database; config: Config; accounts: AccountModule; jobs: JobQueue; gates: ProviderGate; gateway: MailGateway;
}, signal: AbortSignal) {
  const workerId = `worker-${randomUUID()}`;
  await Promise.all(Array.from({ length: deps.config.globalMailConcurrency }, (_, index) => loop(`${workerId}-${index}`, deps, signal)));
}

async function loop(workerId: string, deps: {
  db: Database; config: Config; accounts: AccountModule; jobs: JobQueue; gates: ProviderGate; gateway: MailGateway;
}, signal: AbortSignal) {
  const leaseMs = deps.config.taskTimeoutMs + 60_000;
  while (!signal.aborted) {
    const task = await deps.jobs.claim(workerId, leaseMs);
    if (!task) { await wait(750, signal); continue; }
    const account = await deps.accounts.getForWorker(task.account_id) as WorkerAccount | null;
    if (!account) {
      await deps.jobs.fail(task, workerId, { code: "ACCOUNT_UNAVAILABLE", summary: "Account is missing or disabled" }, null);
      continue;
    }
    const provider = account.provider as Provider;
    const gate = await deps.gates.acquire(provider, workerId, leaseMs);
    if (!gate.granted) {
      await deps.jobs.defer(task.id, workerId, gate.retryAt);
      continue;
    }
    try {
      await execute(deps.gateway, account, task);
      await deps.gates.success(provider);
      await deps.jobs.succeed(task.id, workerId);
    } catch (cause) {
      const error = redactError(cause);
      const decision = retryDecision(error.code, task.attempts);
      await deps.gates.failure(provider, error.code, decision.pauseProviderMs);
      await deps.db.query(
        "UPDATE mail_accounts SET status='degraded',last_error_code=$2,last_error_summary=$3,updated_at=now() WHERE id=$1",
        [account.id, error.code, error.summary]);
      await deps.jobs.fail(task, workerId, error, decision.retry ? decision.delayMs : null);
    } finally {
      await deps.gates.release(provider, workerId);
    }
  }
}

async function execute(gateway: MailGateway, account: WorkerAccount, task: MailTask) {
  switch (task.kind) {
    case "validate": return gateway.validate(account);
    case "sync": return gateway.sync(account);
    case "send": return gateway.send(account, task.payload);
    case "content": return gateway.fetchContent(account, task.payload);
  }
}

function wait(ms: number, signal: AbortSignal) {
  return new Promise<void>((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => { clearTimeout(timer); resolve(); }, { once: true });
  });
}
