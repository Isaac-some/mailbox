import type { Config } from "./config.js";
import type { Database } from "./db/database.js";
import { MailGateway } from "./mail/gateway.js";
import { AccountModule } from "./modules/accounts.js";
import { AuditLog } from "./modules/audit.js";
import { AuthModule } from "./modules/auth.js";
import { JobQueue } from "./modules/jobs.js";
import { MessageModule } from "./modules/messages.js";
import { OperationsModule } from "./modules/operations.js";
import { ProviderGate } from "./modules/provider-gate.js";
import { CredentialVault } from "./security/secrets.js";

export function buildContext(db: Database, config: Config, credentialKey: Buffer, sessionKey: Buffer) {
  const audit = new AuditLog(db);
  const vault = new CredentialVault(credentialKey);
  const accounts = new AccountModule(db, vault, audit);
  const jobs = new JobQueue(db);
  const gates = new ProviderGate(db, config.providerConnectIntervalMs, config.providerPauseThreshold);
  return {
    db, config, audit, accounts, jobs, gates,
    auth: new AuthModule(db, sessionKey, audit),
    messages: new MessageModule(db),
    operations: new OperationsModule(db),
    gateway: new MailGateway(db, config),
    sessionKey
  };
}

export type AppContext = ReturnType<typeof buildContext>;
