import { describe, expect, it } from "vitest";
import { motionCovers, sampleMotion } from "./motion";
import type { CatalogMotion } from "./motion";
import { interpolatePosition } from "./orbits";

const start = Date.parse("2026-10-14T12:00:00Z");
// Independent cubic motion: x=t³, y=2t², z=7e6+t. Hermite must reproduce it exactly.
const at = (t: number) => [t ** 3, 2 * t * t, 7e6 + t, 3 * t * t, 4 * t, 1];
const object = {
  orbitalObjectId: "a",
  orbitVersion: 2,
  states: new Float64Array([0, 30, 60, 90, 120].flatMap(at)),
};
const buffer: CatalogMotion = {
  id: "buffer",
  startUtc: new Date(start).toISOString(),
  endUtc: new Date(start + 120000).toISOString(),
  start,
  end: start + 120000,
  stepSeconds: 30,
  catalogCount: 1,
  objects: [object],
};

describe("catalog animation interpolation", () => {
  it("uses velocities to reproduce curved motion between backend samples", () => {
    for (const seconds of [
      0, 0.1, 1.5, 15, 29.99, 30, 30.01, 75, 119.99, 120,
    ]) {
      const result = sampleMotion(buffer, object, start + seconds * 1000, {
        x: 0,
        y: 0,
        z: 0,
      })!;
      expect(result.x).toBeCloseTo(seconds ** 3, 5);
      expect(result.y).toBeCloseTo(2 * seconds * seconds, 5);
      expect(result.z).toBeCloseTo(7e6 + seconds, 5);
    }
  });
  it("never extrapolates before or after the available window", () => {
    for (const time of [start - 1, start + 120001]) {
      expect(motionCovers(buffer, time)).toBe(false);
      expect(
        sampleMotion(buffer, object, time, { x: 0, y: 0, z: 0 }),
      ).toBeUndefined();
    }
  });
  it("is continuous across overlapping buffer refreshes", () => {
    const nextObject = {
      ...object,
      states: new Float64Array([60, 90, 120, 150, 180].flatMap(at)),
    };
    const next = {
      ...buffer,
      start: start + 60000,
      end: start + 180000,
      objects: [nextObject],
    };
    for (const seconds of [60, 61.3, 75, 119.9]) {
      const a = sampleMotion(next, nextObject, start + seconds * 1000, {
        x: 0,
        y: 0,
        z: 0,
      })!;
      const b = sampleMotion(buffer, object, start + seconds * 1000, {
        x: 0,
        y: 0,
        z: 0,
      })!;
      for (const axis of ["x", "y", "z"] as const)
        expect(a[axis]).toBeCloseTo(b[axis], 6);
    }
  });
  it("uses the same interpolation for the selected trajectory and catalog marker", () => {
    const trajectory = {
      orbitalObjectId: "a",
      orbitVersion: 2,
      startUtc: buffer.startUtc,
      endUtc: buffer.endUtc,
      stepSeconds: 30,
      points: [0, 30, 60, 90, 120].map((t) => ({
        timestampUtc: new Date(start + t * 1000).toISOString(),
        positionEcefMeters: { x: at(t)[0], y: at(t)[1], z: at(t)[2] },
        velocityEcefMetersPerSecond: { x: at(t)[3], y: at(t)[4], z: at(t)[5] },
      })),
    };
    for (const seconds of [0, 15, 47, 120])
      expect(interpolatePosition(trajectory, start + seconds * 1000)).toEqual(
        sampleMotion(buffer, object, start + seconds * 1000, {
          x: 0,
          y: 0,
          z: 0,
        }),
      );
  });
});
