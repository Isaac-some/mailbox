export interface RetryDecision { retry: boolean; delayMs: number; pauseProviderMs: number; }

export function retryDecision(code: string, attempt: number, random = Math.random): RetryDecision {
  if (code === "AUTH_FAILED" || code === "TLS_ERROR") return { retry: false, delayMs: 0, pauseProviderMs: 0 };
  const base = code === "RATE_LIMITED" ? 60_000 : 5_000;
  const capped = Math.min(30 * 60_000, base * 2 ** Math.max(0, attempt - 1));
  const delayMs = Math.round(capped * (0.75 + random() * 0.5));
  return { retry: true, delayMs, pauseProviderMs: code === "RATE_LIMITED" ? Math.max(delayMs, 5 * 60_000) : 0 };
}
