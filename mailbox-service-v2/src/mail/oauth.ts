import type { MailCredential } from "../security/secrets.js";

export async function resolveAuth(credential: MailCredential) {
  if (credential.type === "password") return { user: credential.username, pass: credential.secret };
  const body = new URLSearchParams({ grant_type: "refresh_token", refresh_token: credential.refreshToken, client_id: credential.clientId });
  if (credential.clientSecret) body.set("client_secret", credential.clientSecret);
  const response = await fetch(credential.tokenEndpoint, {
    method: "POST", headers: { "content-type": "application/x-www-form-urlencoded" }, body, signal: AbortSignal.timeout(30_000)
  });
  if (!response.ok) throw new Error(`OAuth token request failed (${response.status})`);
  const payload = await response.json() as { access_token?: string };
  if (!payload.access_token) throw new Error("OAuth response has no access token");
  return { user: credential.username, accessToken: payload.access_token };
}

