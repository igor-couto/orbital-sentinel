import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { getRetryDelay, pollResource } from "./pollResource";

beforeEach(() => {
  vi.useFakeTimers();
  vi.spyOn(Math, "random").mockReturnValue(0.5);
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("resource polling recovery", () => {
  it("recovers a failed initial catalog in seconds, then returns to the five-minute interval", async () => {
    const catalog = { items: [{ id: "object-1" }] };
    const load = vi
      .fn()
      .mockRejectedValueOnce(new ApiError("Unavailable", 503))
      .mockResolvedValue(catalog);
    const onValue = vi.fn(),
      onError = vi.fn();
    const controller = new AbortController();
    pollResource(load, 300_000, controller.signal, onValue, onError);

    await vi.advanceTimersByTimeAsync(0);
    expect(onError).toHaveBeenCalledOnce();
    expect(onValue).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1_800);
    expect(onValue).toHaveBeenCalledWith(catalog);
    expect(load).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(299_999);
    expect(load).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(load).toHaveBeenCalledTimes(3);
    controller.abort();
  });

  it("backs off repeated failures and resets the retry delay after recovery", async () => {
    const error = new ApiError("Offline", 0);
    const load = vi
      .fn()
      .mockRejectedValueOnce(error)
      .mockRejectedValueOnce(error)
      .mockResolvedValueOnce("catalog")
      .mockRejectedValueOnce(error)
      .mockResolvedValue("updated");
    const onValue = vi.fn();
    const controller = new AbortController();
    pollResource(load, 300_000, controller.signal, onValue, vi.fn());
    await vi.advanceTimersByTimeAsync(1_800);
    expect(load).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(3_600);
    expect(onValue).toHaveBeenLastCalledWith("catalog");
    await vi.advanceTimersByTimeAsync(300_000);
    // A failed refresh never replaces the usable catalog with an empty value.
    expect(onValue).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1_800);
    expect(onValue).toHaveBeenLastCalledWith("updated");
    controller.abort();
  });

  it("caps retries and respects server Retry-After instead of hammering a busy service", async () => {
    expect(getRetryDelay(new ApiError("Offline", 0), 1_000, 300_000)).toBe(
      27_000,
    );
    const load = vi.fn().mockRejectedValue(new ApiError("Busy", 429, 60_000));
    const controller = new AbortController();
    pollResource(load, 300_000, controller.signal, vi.fn(), vi.fn());
    await vi.advanceTimersByTimeAsync(59_999);
    expect(load).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(1);
    expect(load).toHaveBeenCalledTimes(2);
    controller.abort();
  });

  it("does not rapidly retry permanent request failures", () => {
    for (const status of [400, 401, 403, 404, 422])
      expect(getRetryDelay(new ApiError("Invalid", status), 1, 300_000)).toBe(
        300_000,
      );
  });

  it("cancels a queued retry when the view is disposed or manually refreshed", async () => {
    const load = vi.fn().mockRejectedValue(new ApiError("Offline", 0));
    const controller = new AbortController();
    pollResource(load, 300_000, controller.signal, vi.fn(), vi.fn());
    await vi.advanceTimersByTimeAsync(0);
    controller.abort();
    await vi.advanceTimersByTimeAsync(600_000);
    expect(load).toHaveBeenCalledOnce();
  });

  it("does not overlap slow requests or publish responses after cancellation", async () => {
    let complete!: (value: string) => void;
    const load = vi.fn(
      () =>
        new Promise<string>((resolve) => {
          complete = resolve;
        }),
    );
    const onValue = vi.fn(),
      onError = vi.fn();
    const controller = new AbortController();
    pollResource(load, 300_000, controller.signal, onValue, onError);
    await vi.advanceTimersByTimeAsync(600_000);
    expect(load).toHaveBeenCalledOnce();
    controller.abort();
    complete("late result");
    await vi.advanceTimersByTimeAsync(600_000);
    expect(onValue).not.toHaveBeenCalled();
    expect(onError).not.toHaveBeenCalled();
    expect(load).toHaveBeenCalledOnce();
  });
});
