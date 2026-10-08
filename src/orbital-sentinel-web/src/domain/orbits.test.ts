import { describe, expect, it } from "vitest";
import {
  elementAge,
  interpolatePosition,
  isCurrentPosition,
  objectKind,
} from "./orbits";
import type { Trajectory } from "./orbits";

const start = Date.parse("2026-10-07T12:00:00Z");
const trajectory: Trajectory = {
  orbitalObjectId: "internal-identity",
  orbitVersion: 3,
  startUtc: new Date(start).toISOString(),
  endUtc: new Date(start + 60_000).toISOString(),
  stepSeconds: 60,
  points: [
    {
      timestampUtc: new Date(start).toISOString(),
      positionEcefMeters: { x: 7_000_000, y: 0, z: -10 },
    },
    {
      timestampUtc: new Date(start + 60_000).toISOString(),
      positionEcefMeters: { x: 6_900_000, y: 20_000, z: 10 },
    },
  ],
};

describe("visualization interpolation", () => {
  it("interpolates ECEF metres between authoritative samples without changing frames", () => {
    expect(interpolatePosition(trajectory, start + 30_000)).toEqual({
      x: 6_950_000,
      y: 10_000,
      z: 0,
    });
  });
  it("includes the exact first and last sample", () => {
    expect(interpolatePosition(trajectory, start)).toEqual(
      trajectory.points[0].positionEcefMeters,
    );
    expect(interpolatePosition(trajectory, start + 60_000)).toEqual(
      trajectory.points[1].positionEcefMeters,
    );
  });
  it("never extrapolates outside a prediction window", () => {
    expect(interpolatePosition(trajectory, start - 1)).toBeUndefined();
    expect(interpolatePosition(trajectory, start + 60_001)).toBeUndefined();
    expect(
      interpolatePosition({ ...trajectory, points: [] }, start),
    ).toBeUndefined();
  });
});

describe("data freshness", () => {
  it("hides samples outside the two-minute current-position tolerance, including future samples", () => {
    const position = {
      ...trajectory.points[0],
      orbitalObjectId: trajectory.orbitalObjectId,
    };
    expect(isCurrentPosition(position, start + 120_000)).toBe(true);
    expect(isCurrentPosition(position, start + 120_001)).toBe(false);
    expect(isCurrentPosition(position, start - 120_001)).toBe(false);
  });
  it("flags old orbital elements independently of ingestion or prediction time", () => {
    expect(elementAge(trajectory.startUtc, start + 47 * 3_600_000)).toEqual({
      label: "47h old",
      stale: false,
    });
    expect(elementAge(trajectory.startUtc, start + 48 * 3_600_000)).toEqual({
      label: "2d old",
      stale: true,
    });
    expect(elementAge(trajectory.startUtc, start - 1)).toEqual({
      label: "Future epoch",
      stale: false,
    });
  });
});

describe("object classification", () => {
  it("recognizes common API casing without guessing unknown classifications", () => {
    expect(objectKind("PAYLOAD")).toBe("payload");
    expect(objectKind("Rocket Body")).toBe("rocket");
    expect(objectKind("rocket_body")).toBe("rocket");
    expect(objectKind("Debris")).toBe("debris");
    expect(objectKind("UNKNOWN")).toBe("unknown");
  });
});
