import { afterEach, describe, expect, it, vi } from "vitest";
import { createMotionLoader, parseCatalogMotion } from "./client";

const payload = {
  id: "buffer",
  startUtc: "2026-10-14T12:00:00Z",
  endUtc: "2026-10-14T12:02:00Z",
  stepSeconds: 30,
  catalogCount: 1,
  objects: [
    {
      orbitalObjectId: "a",
      orbitVersion: 3,
      states: Array.from({ length: 30 }, (_, i) => i),
    },
  ],
};
afterEach(() => vi.unstubAllGlobals());

describe("motion API contract", () => {
  it("prepares finite packed samples once, preserving object identity and revision", () => {
    const result = parseCatalogMotion(payload);
    expect(result.objects[0].states).toBeInstanceOf(Float64Array);
    expect(result.objects[0].orbitalObjectId).toBe("a");
    expect(result.objects[0].orbitVersion).toBe(3);
    expect(result.end - result.start).toBe(120000);
  });
  it("rejects malformed windows, missing states, duplicated identities and non-finite values", () => {
    for (const bad of [
      { ...payload, stepSeconds: 0 },
      { ...payload, endUtc: payload.startUtc },
      { ...payload, endUtc: "2026-10-14T12:02:01Z" },
      { ...payload, objects: [{ ...payload.objects[0], states: [1, 2, 3] }] },
      {
        ...payload,
        objects: [
          {
            ...payload.objects[0],
            states: payload.objects[0].states.map((n, i) =>
              i === 0 ? NaN : n,
            ),
          },
        ],
      },
      {
        ...payload,
        catalogCount: 2,
        objects: [...payload.objects, ...payload.objects],
      },
    ])
      expect(() => parseCatalogMotion(bad)).toThrow();
  });
  it("reuses a 304 response without downloading or replacing the current buffer", async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify(payload), {
          headers: { ETag: '"buffer"' },
        }),
      )
      .mockResolvedValueOnce(new Response(null, { status: 304 }));
    vi.stubGlobal("fetch", fetch);
    const load = createMotionLoader(),
      signal = new AbortController().signal;
    const initial = await load(signal);
    expect(await load(signal)).toBe(initial);
    expect(fetch.mock.calls[1][1].headers["If-None-Match"]).toBe('"buffer"');
  });
  it("keeps a good conditional cache after a malformed refresh", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(
          new Response(JSON.stringify(payload), {
            headers: { ETag: '"buffer"' },
          }),
        )
        .mockResolvedValueOnce(
          new Response("{}", { headers: { ETag: '"bad"' } }),
        )
        .mockResolvedValueOnce(new Response(null, { status: 304 })),
    );
    const load = createMotionLoader(),
      signal = new AbortController().signal;
    const initial = await load(signal);
    await expect(load(signal)).rejects.toThrow();
    expect(await load(signal)).toBe(initial);
  });
});
