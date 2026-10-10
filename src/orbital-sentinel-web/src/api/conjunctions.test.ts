import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "./client";
import {
  createSimulation,
  getConjunctions,
  getPredictionRun,
  parseConjunctionDetail,
  parseConjunctionPage,
  parseConjunctionVisualization,
  parsePredictionRun,
} from "./conjunctions";
import type {
  ConjunctionAssessment,
  ConjunctionSummary,
  ConjunctionVisualization,
  PredictionRun,
} from "../domain/conjunctions";
import type { OrbitSnapshot, Trajectory } from "../domain/orbits";

const start = "2026-01-01T00:00:00Z";
const end = "2026-01-01T00:01:00Z";
const run: PredictionRun = {
  id: "run",
  source: "Simulation",
  status: "Completed",
  createdAtUtc: start,
  predictionStartUtc: start,
  predictionEndUtc: end,
  clockUtc: start,
  objectCount: 2,
};
const assessment: ConjunctionAssessment = {
  id: "assessment",
  predictionRunId: "run",
  timeOfClosestApproachUtc: "2026-01-01T00:00:30Z",
  missDistanceMeters: 680,
  relativeVelocityMetersPerSecond: 11000,
  detectedAtUtc: start,
  primaryOrbitVersion: 2,
  secondaryOrbitVersion: 3,
  isWindowBoundary: false,
  isExtendedProximity: false,
};
const encounter: ConjunctionSummary = {
  id: "conjunction",
  source: "Simulation",
  primaryObjectId: "a",
  secondaryObjectId: "b",
  primaryName: "Simulation A",
  secondaryName: "Simulation B",
  latestAssessment: assessment,
};
function snapshot(id: string, version: number): OrbitSnapshot {
  return {
    object: {
      id,
      name: id,
      objectType: "Payload",
      noradCatalogId: null,
      internationalDesignator: null,
      updatedAt: start,
    },
    version,
    fingerprint: "fingerprint",
    ingestedAt: start,
    elements: {
      epochUtc: start,
      meanMotionRevolutionsPerDay: 15,
      eccentricity: 0,
      inclinationDegrees: 30,
      rightAscensionDegrees: 0,
      argumentOfPericenterDegrees: 0,
      meanAnomalyDegrees: 0,
      bStar: 0,
      meanMotionDot: 0,
      meanMotionDdot: 0,
    },
  };
}
function trajectory(id: string, version: number): Trajectory {
  return {
    orbitalObjectId: id,
    orbitVersion: version,
    startUtc: start,
    endUtc: end,
    stepSeconds: 60,
    points: [start, end].map((timestampUtc) => ({
      timestampUtc,
      positionEcefMeters: { x: 7e6, y: 0, z: 0 },
    })),
  };
}
const view: ConjunctionVisualization = {
  assessmentId: assessment.id,
  predictionRunId: "run",
  source: "Simulation",
  timeOfClosestApproachUtc: assessment.timeOfClosestApproachUtc,
  primary: snapshot("a", 2),
  secondary: snapshot("b", 3),
  primaryTrajectory: trajectory("a", 2),
  secondaryTrajectory: trajectory("b", 3),
  primaryPositionEcefMeters: { x: 7e6, y: 0, z: 0 },
  secondaryPositionEcefMeters: { x: 7e6, y: 680, z: 0 },
};
const page = { items: [encounter], totalCount: 1, page: 1, pageSize: 30 };
afterEach(() => vi.unstubAllGlobals());

describe("conjunction API validation", () => {
  it("accepts complete, unit-labelled encounters and explicit run states", () => {
    expect(parseConjunctionPage(page, "Simulation", "run").items).toHaveLength(
      1,
    );
    expect(
      parseConjunctionDetail(
        { ...encounter, assessments: [assessment] },
        "conjunction",
      ).assessments,
    ).toHaveLength(1);
    expect(parseConjunctionVisualization(view, assessment, encounter)).toEqual(
      view,
    );
    for (const status of ["Pending", "Running", "Completed", "Failed"])
      expect(parsePredictionRun({ ...run, status }).status).toBe(status);
  });
  it("rejects simulation data in the real list and assessments from another prediction run", () => {
    expect(() => parseConjunctionPage(page, "Real")).toThrow(ApiError);
    expect(() =>
      parseConjunctionPage(page, "Simulation", "different-run"),
    ).toThrow(ApiError);
    expect(() =>
      parseConjunctionDetail(
        { ...encounter, assessments: [assessment] },
        "another-encounter",
      ),
    ).toThrow(ApiError);
  });
  it("rejects invalid numeric results, same-object pairs and ambiguous status", () => {
    for (const missDistanceMeters of [-1, Infinity, Number.NaN])
      expect(() =>
        parseConjunctionPage(
          {
            ...page,
            items: [
              {
                ...encounter,
                latestAssessment: { ...assessment, missDistanceMeters },
              },
            ],
          },
          "Simulation",
        ),
      ).toThrow(ApiError);
    expect(() =>
      parseConjunctionPage(
        { ...page, items: [{ ...encounter, secondaryObjectId: "a" }] },
        "Simulation",
      ),
    ).toThrow(ApiError);
    expect(() => parsePredictionRun({ ...run, status: "Success" })).toThrow(
      ApiError,
    );
    expect(() =>
      parsePredictionRun({ ...run, predictionEndUtc: start }),
    ).toThrow(ApiError);
  });
  it("rejects mismatched source, object IDs, element versions, trajectory versions and non-finite TCA locations", () => {
    const invalid = [
      { ...view, source: "Real" },
      { ...view, assessmentId: "other" },
      { ...view, predictionRunId: "other" },
      { ...view, primary: snapshot("b", 2) },
      { ...view, primary: snapshot("a", 3) },
      { ...view, secondaryTrajectory: trajectory("b", 2) },
      { ...view, secondaryPositionEcefMeters: { x: Infinity, y: 0, z: 0 } },
    ];
    for (const value of invalid)
      expect(() =>
        parseConjunctionVisualization(value, assessment, encounter),
      ).toThrow(ApiError);
  });
  it("rejects TCA outside its pinned prediction window", () => {
    const outside = {
      ...assessment,
      timeOfClosestApproachUtc: "2026-01-02T00:00:00Z",
    };
    expect(() =>
      parseConjunctionVisualization(
        { ...view, timeOfClosestApproachUtc: outside.timeOfClosestApproachUtc },
        outside,
        encounter,
      ),
    ).toThrow(ApiError);
  });
});

describe("conjunction requests", () => {
  it("treats a missing latest run as awaiting data, but preserves errors on a specifically requested run", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ title: "Prediction run not found." }), {
          status: 404,
        }),
      ),
    );
    expect(
      await getPredictionRun("Real", new AbortController().signal),
    ).toBeNull();
    await expect(
      getPredictionRun("Real", new AbortController().signal, "specific"),
    ).rejects.toThrow(ApiError);
  });
  it("keeps POST retry identity in a header and refuses a real run returned by the simulation endpoint", async () => {
    const fetch = vi
      .fn()
      .mockImplementation(() =>
        Promise.resolve(new Response(JSON.stringify(run), { status: 202 })),
      );
    vi.stubGlobal("fetch", fetch);
    const signal = new AbortController().signal;
    await createSimulation("same-key", signal);
    await createSimulation("same-key", signal);
    for (const call of fetch.mock.calls)
      expect(call[1]).toMatchObject({
        method: "POST",
        headers: { "Idempotency-Key": "same-key" },
      });
    fetch.mockImplementation(() =>
      Promise.resolve(
        new Response(JSON.stringify({ ...run, source: "Real" }), {
          status: 202,
        }),
      ),
    );
    await expect(createSimulation("same-key", signal)).rejects.toThrow(
      ApiError,
    );
  });
  it("passes the virtual clock and run boundary explicitly when filtering simulation results", async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(page)));
    vi.stubGlobal("fetch", fetch);
    await getConjunctions(
      "Simulation",
      "run",
      1,
      start,
      new AbortController().signal,
    );
    const url = new URL(fetch.mock.calls[0][0], "http://localhost");
    expect(url.searchParams.get("source")).toBe("Simulation");
    expect(url.searchParams.get("predictionRunId")).toBe("run");
    expect(url.searchParams.get("from")).toBe(start);
  });
});
