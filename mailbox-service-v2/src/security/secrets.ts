import { createCipheriv, createDecipheriv, createHash, randomBytes, scrypt as scryptCallback, timingSafeEqual } from "node:crypto";
import { promisify } from "node:util";

const scrypt = promisify(scryptCallback);
const CIPHER_VERSION = "v1";

export type MailCredential =
  | { type: "password"; username: string; secret: string }
  | { type: "oauth_refresh"; username: string; refreshToken: string; clientId: string; clientSecret?: string | undefined; tokenEndpoint: string };

export class CredentialVault {
  constructor(private readonly key: Buffer) {
    if (key.length !== 32) throw new Error("Credential key must be 32 bytes");
  }

  encrypt(value: MailCredential): string {
    const nonce = randomBytes(12);
    const cipher = createCipheriv("aes-256-gcm", this.key, nonce);
    cipher.setAAD(Buffer.from(CIPHER_VERSION));
    const body = Buffer.concat([cipher.update(JSON.stringify(value), "utf8"), cipher.final()]);
    const tag = cipher.getAuthTag();
    return [CIPHER_VERSION, nonce.toString("base64url"), tag.toString("base64url"), body.toString("base64url")].join(".");
  }

  decrypt(envelope: string): MailCredential {
    const [version, nonceText, tagText, bodyText] = envelope.split(".");
    if (version !== CIPHER_VERSION || !nonceText || !tagText || !bodyText) throw new Error("Unsupported credential envelope");
    const decipher = createDecipheriv("aes-256-gcm", this.key, Buffer.from(nonceText, "base64url"));
    decipher.setAAD(Buffer.from(version));
    decipher.setAuthTag(Buffer.from(tagText, "base64url"));
    return JSON.parse(Buffer.concat([decipher.update(Buffer.from(bodyText, "base64url")), decipher.final()]).toString("utf8")) as MailCredential;
  }
}

export async function hashPassword(password: string): Promise<string> {
  const salt = randomBytes(16);
  const derived = await scrypt(password, salt, 64) as Buffer;
  return `scrypt$${salt.toString("base64url")}$${derived.toString("base64url")}`;
}

export async function verifyPassword(password: string, stored: string): Promise<boolean> {
  const [algorithm, saltText, hashText] = stored.split("$");
  if (algorithm !== "scrypt" || !saltText || !hashText) return false;
  const expected = Buffer.from(hashText, "base64url");
  const actual = await scrypt(password, Buffer.from(saltText, "base64url"), expected.length) as Buffer;
  return expected.length === actual.length && timingSafeEqual(expected, actual);
}

export function hashOpaque(value: string, key: Buffer): string {
  return createHash("sha256").update(key).update(value).digest("base64url");
}

export function redactError(error: unknown): { code: string; summary: string } {
  const source = error instanceof Error ? error.message : String(error);
  const summary = source
    .replace(/(password|pass|token|secret|authorization)\s*[:=]\s*[^\s,;]+/gi, "$1=[REDACTED]")
    .replace(/[A-Za-z0-9_-]{32,}/g, "[REDACTED]")
    .slice(0, 400);
  return { code: classifyError(source), summary };
}

function classifyError(message: string) {
  if (/auth|credential|login|password|invalid credentials/i.test(message)) return "AUTH_FAILED";
  if (/429|rate.?limit|too many/i.test(message)) return "RATE_LIMITED";
  if (/timeout|timed out|ETIMEDOUT/i.test(message)) return "TIMEOUT";
  if (/certificate|TLS|SSL/i.test(message)) return "TLS_ERROR";
  if (/ENOTFOUND|ECONNREFUSED|network|socket/i.test(message)) return "NETWORK_ERROR";
  return "MAIL_ERROR";
}
