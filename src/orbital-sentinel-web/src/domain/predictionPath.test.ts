import { describe, expect, it } from "vitest";
import type { Trajectory } from "./orbits";
import { interpolatePosition } from "./orbits";
import { predictionPath } from "./predictionPath";

const start = Date.parse("2026-10-14T12:00:00Z");
const hour = 3_600_000;
const trajectory: Trajectory = {
  orbitalObjectId: "long-period-object",
  orbitVersion: 1,
  startUtc: new Date(start).toISOString(),
  endUtc: new Date(start + 24 * hour).toISOString(),
  stepSeconds: 60,
  points: Array.from({ length: 1441 }, (_, i) => ({
    timestampUtc: new Date(start + i * 60_000).toISOString(),
    positionEcefMeters: { x: i * 1000, y: 7_000_000, z: i * 500 },
  })),
};

describe("selected prediction path", () => {
  it("shows a full 15-hour period instead of the previous three-hour cutoff", () => {
    const period = 1440 / 1.5751094; // 2026-167A, the reported object.
    const now = start + 15_000;
    const path = predictionPath(trajectory, now, period);
    expect(path.length).toBeGreaterThan(900);
    expect(path[0]).toEqual(interpolatePosition(trajectory, now));
    expect(path.at(-1)).toEqual(
      interpolatePosition(trajectory, now + period * 60_000),
    );
    expect(path.at(-1)).not.toEqual(path[0]);
  });

  it("includes exact endpoints without duplicating samples at grid boundaries", () => {
    const path = predictionPath(trajectory, start, 90);
    expect(path).toEqual(
      trajectory.points.slice(0, 91).map((p) => p.positionEcefMeters),
    );
  });

  it("keeps the marker attached while advancing between samples", () => {
    for (const seconds of [0, 1, 59, 60, 61, 119]) {
      const time = start + seconds * 1000;
      expect(predictionPath(trajectory, time, 90)[0]).toEqual(
        interpolatePosition(trajectory, time),
      );
    }
  });

  it("uses all available data for periods longer than the prediction window", () => {
    expect(predictionPath(trajectory, start, 2 * 1440)).toEqual(
      trajectory.points.map((p) => p.positionEcefMeters),
    );
  });

  it("clips to the actual final sample without extrapolating or drawing stale paths", () => {
    const path = predictionPath(trajectory, start + 24 * hour - 30_000, 90);
    expect(path).toHaveLength(2);
    expect(path.at(-1)).toEqual(trajectory.points.at(-1)?.positionEcefMeters);
    for (const time of [start - 1, start + 24 * hour, start + 24 * hour + 1])
      expect(predictionPath(trajectory, time, 90)).toEqual([]);
    expect(predictionPath({ ...trajectory, points: [] }, start, 90)).toEqual(
      [],
    );
  });
});
