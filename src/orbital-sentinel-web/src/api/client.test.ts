import { afterEach, describe, expect, it, vi } from "vitest";
import {
  ApiError,
  getCatalog,
  getTrajectory,
  parseCatalog,
  parsePositions,
  parseTrajectory,
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
