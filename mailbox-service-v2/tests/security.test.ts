import { randomBytes } from "node:crypto";
import { describe, expect, it } from "vitest";
import { CredentialVault, hashPassword, verifyPassword } from "../src/security/secrets.js";

describe("credential vault", () => {
  it("round-trips credentials without storing plaintext", () => {
    const vault = new CredentialVault(randomBytes(32));
    const credential = { type: "password" as const, username: "user@example.com", secret: "app-password-123" };
    const envelope = vault.encrypt(credential);
    expect(envelope).not.toContain(credential.secret);
    expect(vault.decrypt(envelope)).toEqual(credential);
  });

  it("rejects tampered ciphertext", () => {
    const vault = new CredentialVault(randomBytes(32));
    const envelope = vault.encrypt({ type: "password", username: "u", secret: "s" });
    const parts = envelope.split("."); parts[3] = `${parts[3]}x`;
    expect(() => vault.decrypt(parts.join("."))).toThrow();
  });

  it("hashes and verifies passwords", async () => {
    const stored = await hashPassword("long-test-password");
    expect(stored).not.toContain("long-test-password");
    expect(await verifyPassword("long-test-password", stored)).toBe(true);
    expect(await verifyPassword("wrong-password", stored)).toBe(false);
  });
});
