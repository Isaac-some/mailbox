import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { z } from "zod";

const schema = z.object({
  NODE_ENV: z.enum(["development", "test", "production"]).default("development"),
  HOST: z.string().default("127.0.0.1"),
  PORT: z.coerce.number().int().min(1).max(65535).default(4300),
  DATABASE_URL: z.string().min(1).default("pglite://.data/local-postgres"),
  CREDENTIAL_KEY_FILE: z.string().default("./secrets/credential.key"),
  SESSION_KEY_FILE: z.string().default("./secrets/session.key"),
  INITIAL_ADMIN_USERNAME: z.string().min(3).default("admin"),
  INITIAL_ADMIN_PASSWORD: z.string().min(12).optional(),
  PUBLIC_ORIGIN: z.string().url().default("http://127.0.0.1:4300"),
  TRUST_PROXY: z.enum(["true", "false"]).default("false"),
  GLOBAL_MAIL_CONCURRENCY: z.coerce.number().int().min(1).max(4).default(2),
  PROVIDER_CONNECT_INTERVAL_MS: z.coerce.number().int().min(3000).default(3000),
  PROVIDER_PAUSE_THRESHOLD: z.coerce.number().int().min(1).default(3),
  MAIL_TASK_TIMEOUT_MS: z.coerce.number().int().min(30000).default(180000),
  SYNC_LOOKBACK_DAYS: z.coerce.number().int().min(1).max(90).default(30),
  MAX_MESSAGES_PER_ACCOUNT: z.coerce.number().int().min(1).max(100).default(30)
});

export type Config = ReturnType<typeof loadConfig>;

export function loadConfig(env: NodeJS.ProcessEnv = process.env) {
  const value = schema.parse(env);
  if (value.NODE_ENV === "production" && value.DATABASE_URL.startsWith("pglite://")) {
    throw new Error("Production requires a PostgreSQL DATABASE_URL; PGlite is development-only");
  }
  return {
    env: value.NODE_ENV,
    host: value.HOST,
    port: value.PORT,
    databaseUrl: value.DATABASE_URL,
    credentialKeyFile: resolve(value.CREDENTIAL_KEY_FILE),
    sessionKeyFile: resolve(value.SESSION_KEY_FILE),
    initialAdminUsername: value.INITIAL_ADMIN_USERNAME,
    initialAdminPassword: value.INITIAL_ADMIN_PASSWORD,
    publicOrigin: value.PUBLIC_ORIGIN.replace(/\/$/, ""),
    trustProxy: value.TRUST_PROXY === "true",
    globalMailConcurrency: value.GLOBAL_MAIL_CONCURRENCY,
    providerConnectIntervalMs: value.PROVIDER_CONNECT_INTERVAL_MS,
    providerPauseThreshold: value.PROVIDER_PAUSE_THRESHOLD,
    taskTimeoutMs: value.MAIL_TASK_TIMEOUT_MS,
    syncLookbackDays: value.SYNC_LOOKBACK_DAYS,
    maxMessagesPerAccount: value.MAX_MESSAGES_PER_ACCOUNT
  };
}

export function readSecretFile(path: string, expectedBytes = 32): Buffer {
  const raw = readFileSync(path, "utf8").trim();
  const value = Buffer.from(raw, "base64url");
  if (value.length !== expectedBytes) throw new Error(`${path} must contain ${expectedBytes} random bytes encoded as base64url`);
  return value;
}
