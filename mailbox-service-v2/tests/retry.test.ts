import { describe, expect, it } from "vitest";
import { retryDecision } from "../src/domain/retry.js";

describe("mail retry policy", () => {
  it("does not retry credential and TLS failures", () => {
    expect(retryDecision("AUTH_FAILED", 1, () => 0.5).retry).toBe(false);
    expect(retryDecision("TLS_ERROR", 1, () => 0.5).retry).toBe(false);
  });

  it("backs off with jitter and pauses rate limited providers", () => {
    const decision = retryDecision("RATE_LIMITED", 2, () => 0.5);
    expect(decision.retry).toBe(true);
    expect(decision.delayMs).toBe(120_000);
    expect(decision.pauseProviderMs).toBeGreaterThanOrEqual(300_000);
  });

  it("caps long transient backoff", () => {
    expect(retryDecision("TIMEOUT", 99, () => 0).delayMs).toBe(1_350_000);
  });
});
