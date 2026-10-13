import { describe, expect, it } from "vitest";
import {
  constellationOperator,
  interpolateFlightMetrics,
  operationalStatus,
} from "./objectDetails";
import type { Trajectory } from "./orbits";
import { parseObjectDetails } from "../api/objectDetails";
import { parseTrajectory } from "../api/client";

const start = Date.parse("2026-10-13T12:00:00Z");
const trajectory: Trajectory = {
  orbitalObjectId: "test",
  orbitVersion: 1,
  startUtc: new Date(start).toISOString(),
  endUtc: new Date(start + 60_000).toISOString(),
  stepSeconds: 60,
  points: [
    {
      timestampUtc: new Date(start).toISOString(),
      positionEcefMeters: { x: 7e6, y: 0, z: 0 },
      altitudeKilometers: 400,
      speedKilometersPerSecond: 7.6,
    },
    {
      timestampUtc: new Date(start + 60_000).toISOString(),
      positionEcefMeters: { x: 7e6, y: 0, z: 0 },
      altitudeKilometers: 420,
      speedKilometersPerSecond: 7.4,
    },
  ],
};

describe("object detail measurements", () => {
  it("interpolates speed from SGP4 values rather than deriving it from displayed positions", () => {
    expect(interpolateFlightMetrics(trajectory, start + 30_000)).toEqual({
      altitudeKilometers: 410,
      speedKilometersPerSecond: 7.5,
    });
    expect(
      interpolateFlightMetrics(trajectory, start)?.altitudeKilometers,
    ).toBe(400);
    expect(
      interpolateFlightMetrics(trajectory, start + 60_000)?.altitudeKilometers,
    ).toBe(420);
  });
  it("does not extrapolate or turn legacy missing metrics into zeros", () => {
    expect(interpolateFlightMetrics(trajectory, start - 1)).toBeNull();
    expect(interpolateFlightMetrics(trajectory, start + 60_001)).toBeNull();
    expect(interpolateFlightMetrics(trajectory, NaN)).toBeNull();
    const legacy = {
      ...trajectory,
      points: trajectory.points.map((p) => ({
        ...p,
        altitudeKilometers: undefined,
        speedKilometersPerSecond: undefined,
      })),
    };
    expect(interpolateFlightMetrics(legacy, start + 1000)).toEqual({
      altitudeKilometers: null,
      speedKilometersPerSecond: null,
    });
    expect(parseTrajectory(legacy, "test")).toEqual(legacy);
  });
  it("rejects malformed scalar metrics before rendering", () => {
    for (const speed of [-1, NaN, "7.6"]) {
      const invalid = structuredClone(trajectory);
      Object.assign(invalid.points[0], { speedKilometersPerSecond: speed });
      expect(() => parseTrajectory(invalid, "test")).toThrow();
    }
  });
  it("does not infer an operator from country, broad groups, or debris", () => {
    expect(constellationOperator(["starlink"], "PAY")).toBe("SpaceX");
    expect(constellationOperator(["oneweb"], "PAY")).toBe("Eutelsat OneWeb");
    expect(constellationOperator(["starlink"], "DEB")).toBeNull();
    expect(constellationOperator(["visual", "stations"], "PAY")).toBeNull();
    expect(operationalStatus["-"]).toBe("Nonoperational");
    expect(operationalStatus["?"]).toBe("Unknown");
  });
});

describe("object details boundary", () => {
  const details = {
    orbitalObjectId: "test",
    orbitVersion: 1,
    elementsSource: "CelesTrak GP (OMM)",
    meanOrbit: {
      periodMinutes: 96,
      perigeeKilometers: 560,
      apogeeKilometers: 580,
    },
    metadata: null,
  };
  it("allows unavailable metadata while retaining orbit information", () => {
    expect(parseObjectDetails(details, "test")).toEqual(details);
  });
  it("rejects the wrong object, impossible apsides, and malformed metadata", () => {
    expect(() => parseObjectDetails(details, "other")).toThrow();
    expect(() =>
      parseObjectDetails(
        {
          ...details,
          meanOrbit: { ...details.meanOrbit, apogeeKilometers: 0 },
        },
        "test",
      ),
    ).toThrow();
    expect(() =>
      parseObjectDetails({ ...details, metadata: { ownerCode: "US" } }, "test"),
    ).toThrow();
  });
});
