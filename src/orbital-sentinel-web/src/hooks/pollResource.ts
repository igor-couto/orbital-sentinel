import { ApiError } from "../api/client";

/** Keep recovery separate from the normal refresh interval. */
export function getRetryDelay(
  error: unknown,
  consecutiveFailures: number,
  intervalMs: number,
): number {
  if (
    error instanceof ApiError &&
    error.status !== 0 &&
    error.status !== 408 &&
    error.status !== 429 &&
    error.status < 500
  )
    return intervalMs;

  const backoff = Math.min(
    30_000,
    2_000 * 2 ** Math.min(Math.max(consecutiveFailures - 1, 0), 4),
  );
  // Spread recovering clients across time without exceeding the backoff cap.
  const delay = Math.round(backoff * (0.8 + Math.random() * 0.2));
  return Math.max(
    delay,
    error instanceof ApiError ? (error.retryAfterMs ?? 0) : 0,
  );
}

export function pollResource<T>(
  load: (signal: AbortSignal) => Promise<T>,
  intervalMs: number,
  signal: AbortSignal,
  onValue: (value: T) => void,
  onError: (error: unknown) => void,
): void {
  let failures = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  signal.addEventListener("abort", () => clearTimeout(timer), { once: true });

  async function refresh() {
    if (signal.aborted) return;
    let delay = intervalMs;
    try {
      const value = await load(signal);
      if (signal.aborted) return;
      failures = 0;
      onValue(value);
    } catch (error) {
      if (signal.aborted) return;
      delay = getRetryDelay(error, ++failures, intervalMs);
      onError(error);
    } finally {
      // Schedule after completion: even a slow request cannot overlap its retry.
      if (!signal.aborted) timer = setTimeout(refresh, delay);
    }
  }
  void refresh();
}
