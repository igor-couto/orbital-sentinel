import { afterEach, describe, expect, it, vi } from "vitest";
import {
  ApiError,
  getCatalog,
  getOrbitSnapshot,
  getTrajectory,
  parseCatalog,
  parseCatalogSnapshot,
  parseOrbitSnapshot,
  parsePositions,
  parseTrajectory,
  request,
} from "./client";

const objectId = "a2721318-b2a4-4c77-bff7-3e167f91e9bb";
const catalog = {
  totalCount: 1,
  page: 1,
  pageSize: 200,
  items: [
    {
      object: {
        id: objectId,
        noradCatalogId: null,
        name: "Internal object",
        objectType: "Unknown",
        internationalDesignator: null,
        updatedAt: "2026-10-07T12:00:00Z",
      },
      version: 1,
      fingerprint: "test-fingerprint",
      ingestedAt: "2026-10-07T12:00:00Z",
      elements: {
        epochUtc: "2026-10-07T12:00:00Z",
        meanMotionRevolutionsPerDay: 15,
        eccentricity: 0.001,
        inclinationDegrees: 51.6,
        rightAscensionDegrees: 30,
        argumentOfPericenterDegrees: 40,
        meanAnomalyDegrees: 50,
        bStar: 0,
        meanMotionDot: 0,
        meanMotionDdot: 0,
      },
    },
  ],
};
const trajectory = {
  orbitalObjectId: objectId,
  orbitVersion: 1,
  startUtc: "2026-10-07T12:00:00Z",
  endUtc: "2026-10-07T12:01:00Z",
  stepSeconds: 60,
  points: [
    {
      timestampUtc: "2026-10-07T12:00:00Z",
      positionEcefMeters: { x: 7_000_000, y: 0, z: 0 },
    },
    {
      timestampUtc: "2026-10-07T12:01:00Z",
      positionEcefMeters: { x: 6_900_000, y: 100, z: 0 },
    },
  ],
};

afterEach(() => vi.unstubAllGlobals());

describe("orbital API boundary", () => {
  it("accepts complete orbit geometry and rejects partial or invalid display orientations", () => {
    const view = {
      positionMeters: { x: 7e6, y: 0, z: 0 },
      velocityMetersPerSecond: null,
      rotationToEarthFixedRadians: 0,
    };
    const valid = {
      ...trajectory,
      points: trajectory.points.map((p) => ({ ...p, orbitView: view })),
    };
    expect(parseTrajectory(valid, objectId)).toEqual(valid);
    for (const invalidView of [
      { ...view, rotationToEarthFixedRadians: NaN },
      { ...view, positionMeters: { x: 1 } },
    ])
      expect(() =>
        parseTrajectory(
          {
            ...valid,
            points: valid.points.map((p) => ({ ...p, orbitView: invalidView })),
          },
          objectId,
        ),
      ).toThrow();
    expect(() =>
      parseTrajectory(
        { ...valid, points: [valid.points[0], trajectory.points[1]] },
        objectId,
      ),
    ).toThrow();
  });
  it("bootstraps all 20,225 objects in one compact request without orbital elements", async () => {
    const items = Array.from({ length: 20_225 }, (_, index) => ({
      object: {
        id: `object-${index}`,
        name: `Object ${index}`,
        objectType: "Payload",
        noradCatalogId: index + 1,
        internationalDesignator: null,
      },
      version: 1,
    }));
    const fetchMock = vi
      .fn()
      .mockResolvedValue(Response.json({ items, totalCount: items.length }));
    vi.stubGlobal("fetch", fetchMock);
    const result = await getCatalog(new AbortController().signal);
    expect(result.items).toEqual(items);
    expect(result.totalCount).toBe(20_225);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe("/api/orbital-objects/catalog");
    expect(result.items[0]).not.toHaveProperty("elements");
  });
  it("rejects duplicate identities or an incomplete coherent catalog instead of hiding missing objects", () => {
    const item = { object: catalog.items[0].object, version: 1 };
    expect(() =>
      parseCatalogSnapshot({ items: [item, item], totalCount: 2 }),
    ).toThrow(ApiError);
    expect(() =>
      parseCatalogSnapshot({ items: [item], totalCount: 2 }),
    ).toThrow(ApiError);
    expect(() =>
      parseCatalogSnapshot({ items: [item], totalCount: 0 }),
    ).toThrow(ApiError);
    expect(parseCatalogSnapshot({ items: [], totalCount: 0 })).toEqual({
      items: [],
      totalCount: 0,
    });
  });
  it("rejects malformed compact entries before they reach markers or search", () => {
    const item = { object: catalog.items[0].object, version: 1 };
    for (const invalidItem of [
      { ...item, version: 0 },
      { ...item, version: 1.5 },
      { ...item, object: { ...item.object, id: "" } },
      { ...item, object: { ...item.object, name: null } },
      { ...item, object: { ...item.object, objectType: null } },
      { ...item, object: { ...item.object, noradCatalogId: -1 } },
      { ...item, object: { ...item.object, internationalDesignator: 1 } },
    ])
      expect(() =>
        parseCatalogSnapshot({ items: [invalidItem], totalCount: 1 }),
      ).toThrow(ApiError);
  });
  it("surfaces bootstrap failures so the polling loop can retry without accepting partial data", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response("", { status: 503 })),
    );
    await expect(
      getCatalog(new AbortController().signal),
    ).rejects.toMatchObject({ status: 503 });
  });
  it("fetches complete elements only for the selected identity and rejects another object's response", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(Response.json(catalog.items[0]));
    vi.stubGlobal("fetch", fetchMock);
    expect(
      await getOrbitSnapshot(objectId, new AbortController().signal),
    ).toEqual(catalog.items[0]);
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/orbital-objects/${objectId}`);
    expect(() =>
      parseOrbitSnapshot(catalog.items[0], "another-object"),
    ).toThrow(ApiError);
    expect(() =>
      parseOrbitSnapshot(
        { object: catalog.items[0].object, version: 1 },
        objectId,
      ),
    ).toThrow(ApiError);
  });
  it("accepts an object with an internal identifier and no NORAD identifier", () => {
    expect(parseCatalog(catalog).items[0].object.noradCatalogId).toBeNull();
  });
  it("rejects invalid or physically impossible display elements", () => {
    const invalid = structuredClone(catalog);
    invalid.items[0].elements.meanMotionRevolutionsPerDay = 0;
    expect(() => parseCatalog(invalid)).toThrow(ApiError);
    invalid.items[0].elements.meanMotionRevolutionsPerDay = Number.NaN;
    expect(() => parseCatalog(invalid)).toThrow(ApiError);
  });
  it("rejects malformed coordinates before they reach WebGL", () => {
    expect(() =>
      parsePositions([
        {
          orbitalObjectId: objectId,
          timestampUtc: "2026-10-07T12:00:00Z",
          positionEcefMeters: { x: 1, y: null, z: 0 },
        },
      ]),
    ).toThrow(ApiError);
    expect(() =>
      parsePositions([
        {
          orbitalObjectId: objectId,
          ...trajectory.points[0],
          positionEcefMeters: { x: Infinity, y: 0, z: 0 },
        },
      ]),
    ).toThrow(ApiError);
  });
  it("rejects another object’s trajectory, unordered samples, and inconsistent windows", () => {
    expect(() => parseTrajectory(trajectory, "different-object")).toThrow(
      ApiError,
    );
    expect(() =>
      parseTrajectory(
        { ...trajectory, points: [...trajectory.points].reverse() },
        objectId,
      ),
    ).toThrow(ApiError);
    expect(() =>
      parseTrajectory(
        { ...trajectory, endUtc: "2026-10-07T12:02:00Z" },
        objectId,
      ),
    ).toThrow(ApiError);
    expect(() =>
      parseTrajectory(
        { ...trajectory, points: [trajectory.points[0], trajectory.points[0]] },
        objectId,
      ),
    ).toThrow(ApiError);
  });
  it("accepts a valid sampled trajectory", () => {
    expect(parseTrajectory(trajectory, objectId)).toEqual(trajectory);
  });
  it("treats HTTP 202 as pending and observes a bounded Retry-After", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response('{"status":"pending"}', {
          status: 202,
          headers: { "Retry-After": "10" },
        }),
      ),
    );
    expect(await getTrajectory(objectId, new AbortController().signal)).toEqual(
      { status: "pending", retryAfterMs: 10_000 },
    );
  });
  it.each([
    ["0", 5000],
    ["600", 60_000],
    ["invalid", 10_000],
  ])("bounds a Retry-After of %s", async (header, retryAfterMs) => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(
          new Response("", { status: 202, headers: { "Retry-After": header } }),
        ),
    );
    expect(await getTrajectory(objectId, new AbortController().signal)).toEqual(
      { status: "pending", retryAfterMs },
    );
  });
  it("surfaces a safe ProblemDetails title instead of stack traces or internal details", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            title: "Orbital object not found.",
            detail: "private implementation information",
          }),
          { status: 404 },
        ),
      ),
    );
    await expect(
      getTrajectory(objectId, new AbortController().signal),
    ).rejects.toMatchObject({
      status: 404,
      message: "Orbital object not found.",
    });
  });
  it("reports malformed successful JSON without exposing a response body", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(
          new Response("<internal server output>", { status: 200 }),
        ),
    );
    await expect(
      getCatalog(new AbortController().signal),
    ).rejects.toMatchObject({
      status: 502,
      message: "The service returned an invalid orbital-data response.",
    });
  });
  it.each([429, 503])(
    "preserves Retry-After seconds for HTTP %s",
    async (status) => {
      vi.stubGlobal(
        "fetch",
        vi
          .fn()
          .mockResolvedValue(
            new Response("", { status, headers: { "Retry-After": "45" } }),
          ),
      );
      await expect(
        request("/api/orbital-objects/catalog", new AbortController().signal),
      ).rejects.toMatchObject({ status, retryAfterMs: 45_000 });
    },
  );
  it("preserves a future Retry-After HTTP date", async () => {
    const now = Date.parse("2026-10-15T10:00:00Z");
    const clock = vi.spyOn(Date, "now").mockReturnValue(now);
    try {
      vi.stubGlobal(
        "fetch",
        vi
          .fn()
          .mockResolvedValue(
            new Response("", {
              status: 503,
              headers: { "Retry-After": new Date(now + 60_000).toUTCString() },
            }),
          ),
      );
      await expect(
        request("/api/orbital-objects/catalog", new AbortController().signal),
      ).rejects.toMatchObject({ retryAfterMs: 60_000 });
    } finally {
      clock.mockRestore();
    }
  });
  it.each(["invalid", "-10", "0", "Thu, 01 Jan 1970 00:00:00 GMT"])(
    "ignores an unusable Retry-After value %s",
    async (header) => {
      vi.stubGlobal(
        "fetch",
        vi
          .fn()
          .mockResolvedValue(
            new Response("", {
              status: 503,
              headers: { "Retry-After": header },
            }),
          ),
      );
      await expect(
        request("/api/orbital-objects/catalog", new AbortController().signal),
      ).rejects.toMatchObject({ retryAfterMs: null });
    },
  );
  it("preserves caller cancellation rather than rendering an outage", async () => {
    const controller = new AbortController();
    controller.abort();
    const abortError = new DOMException(
      "The operation was aborted.",
      "AbortError",
    );
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(abortError));
    await expect(getCatalog(controller.signal)).rejects.toBe(abortError);
  });
});
