import { ApiError, parseCatalog, parseTrajectory, request } from "./client";
import type {
  ConjunctionAssessment,
  ConjunctionDetail,
  ConjunctionPage,
  ConjunctionSummary,
  ConjunctionVisualization,
  PredictionRun,
  PredictionSource,
} from "../domain/conjunctions";

const record = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null;
const text = (value: unknown): value is string =>
  typeof value === "string" && value.length > 0;
const date = (value: unknown): value is string =>
  text(value) && Number.isFinite(Date.parse(value));
const finite = (value: unknown): value is number =>
  typeof value === "number" && Number.isFinite(value);
const positiveInteger = (value: unknown): value is number =>
  finite(value) && Number.isSafeInteger(value) && value > 0;
const source = (value: unknown): value is PredictionSource =>
  value === "Real" || value === "Simulation";
const invalid = (): never => {
  throw new ApiError("The service returned invalid conjunction data.", 502);
};

export function parsePredictionRun(value: unknown): PredictionRun {
  if (
    !record(value) ||
    !text(value.id) ||
    !source(value.source) ||
    !["Pending", "Running", "Completed", "Failed"].includes(
      String(value.status),
    ) ||
    !date(value.createdAtUtc) ||
    !date(value.predictionStartUtc) ||
    !date(value.predictionEndUtc) ||
    !date(value.clockUtc) ||
    Date.parse(value.predictionEndUtc) <=
      Date.parse(value.predictionStartUtc) ||
    !finite(value.objectCount) ||
    !Number.isSafeInteger(value.objectCount) ||
    value.objectCount < 0 ||
    (value.unpropagatedObjectCount !== undefined &&
      (!Number.isSafeInteger(value.unpropagatedObjectCount) ||
        (value.unpropagatedObjectCount as number) < 0 ||
        (value.unpropagatedObjectCount as number) > value.objectCount)) ||
    !(
      value.error === undefined ||
      value.error === null ||
      typeof value.error === "string"
    )
  )
    return invalid();
  return value as unknown as PredictionRun;
}

function assessment(value: unknown): value is ConjunctionAssessment {
  return (
    record(value) &&
    text(value.id) &&
    text(value.predictionRunId) &&
    date(value.timeOfClosestApproachUtc) &&
    date(value.detectedAtUtc) &&
    finite(value.missDistanceMeters) &&
    value.missDistanceMeters >= 0 &&
    finite(value.relativeVelocityMetersPerSecond) &&
    value.relativeVelocityMetersPerSecond >= 0 &&
    positiveInteger(value.primaryOrbitVersion) &&
    positiveInteger(value.secondaryOrbitVersion) &&
    typeof value.isWindowBoundary === "boolean" &&
    typeof value.isExtendedProximity === "boolean"
  );
}
function summary(value: unknown): value is ConjunctionSummary {
  return (
    record(value) &&
    text(value.id) &&
    source(value.source) &&
    text(value.primaryObjectId) &&
    text(value.secondaryObjectId) &&
    value.primaryObjectId !== value.secondaryObjectId &&
    text(value.primaryName) &&
    text(value.secondaryName) &&
    assessment(value.latestAssessment)
  );
}
export function parseConjunctionPage(
  value: unknown,
  expectedSource: PredictionSource,
  runId?: string,
): ConjunctionPage {
  if (
    !record(value) ||
    !Array.isArray(value.items) ||
    !value.items.every(summary) ||
    value.items.some(
      (item) =>
        item.source !== expectedSource ||
        (runId && item.latestAssessment.predictionRunId !== runId),
    ) ||
    !positiveInteger(value.page) ||
    !positiveInteger(value.pageSize) ||
    !finite(value.totalCount) ||
    !Number.isSafeInteger(value.totalCount) ||
    value.totalCount < value.items.length
  )
    return invalid();
  return value as unknown as ConjunctionPage;
}
export function parseConjunctionDetail(
  value: unknown,
  id: string,
): ConjunctionDetail {
  if (
    !summary(value) ||
    value.id !== id ||
    !record(value) ||
    !Array.isArray(value.assessments) ||
    !value.assessments.every(assessment) ||
    !value.assessments.length
  )
    return invalid();
  return value as unknown as ConjunctionDetail;
}
export function parseConjunctionVisualization(
  value: unknown,
  selected: ConjunctionAssessment,
  conjunction: ConjunctionSummary,
): ConjunctionVisualization {
  if (
    !record(value) ||
    value.assessmentId !== selected.id ||
    value.predictionRunId !== selected.predictionRunId ||
    value.source !== conjunction.source ||
    value.timeOfClosestApproachUtc !== selected.timeOfClosestApproachUtc ||
    ![value.primaryPositionEcefMeters, value.secondaryPositionEcefMeters].every(
      (point) => record(point) && [point.x, point.y, point.z].every(finite),
    )
  )
    return invalid();
  const snapshots = parseCatalog({
    items: [value.primary, value.secondary],
    page: 1,
    pageSize: 2,
    totalCount: 2,
  }).items;
  if (
    snapshots[0].object.id !== conjunction.primaryObjectId ||
    snapshots[1].object.id !== conjunction.secondaryObjectId ||
    snapshots[0].version !== selected.primaryOrbitVersion ||
    snapshots[1].version !== selected.secondaryOrbitVersion
  )
    return invalid();
  const primaryTrajectory = parseTrajectory(
    value.primaryTrajectory,
    conjunction.primaryObjectId,
  );
  const secondaryTrajectory = parseTrajectory(
    value.secondaryTrajectory,
    conjunction.secondaryObjectId,
  );
  const tca = Date.parse(selected.timeOfClosestApproachUtc);
  if (
    primaryTrajectory.orbitVersion !== selected.primaryOrbitVersion ||
    secondaryTrajectory.orbitVersion !== selected.secondaryOrbitVersion ||
    [primaryTrajectory, secondaryTrajectory].some(
      (trajectory) =>
        tca < Date.parse(trajectory.startUtc) ||
        tca > Date.parse(trajectory.endUtc),
    )
  )
    return invalid();
  return value as unknown as ConjunctionVisualization;
}
async function json(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return invalid();
  }
}
export async function getPredictionRun(
  source: PredictionSource,
  signal: AbortSignal,
  id?: string,
): Promise<PredictionRun | null> {
  try {
    const response = await request(
      id
        ? `/api/prediction-runs/${encodeURIComponent(id)}`
        : `/api/prediction-runs/latest?source=${source}&preferCompleted=true`,
      signal,
    );
    const run = parsePredictionRun(await json(response));
    if (run.source !== source || (id && run.id !== id)) return invalid();
    return run;
  } catch (error) {
    if (!id && error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}
export async function getConjunctions(
  source: PredictionSource,
  runId: string,
  page: number,
  clock: string,
  signal: AbortSignal,
): Promise<ConjunctionPage> {
  const query = new URLSearchParams({
    source,
    predictionRunId: runId,
    page: String(page),
    pageSize: "30",
    from: clock,
  });
  return parseConjunctionPage(
    await json(await request(`/api/conjunctions?${query}`, signal)),
    source,
    runId,
  );
}
export async function getConjunction(
  id: string,
  signal: AbortSignal,
): Promise<ConjunctionDetail> {
  return parseConjunctionDetail(
    await json(
      await request(`/api/conjunctions/${encodeURIComponent(id)}`, signal),
    ),
    id,
  );
}
export async function getConjunctionVisualization(
  selected: ConjunctionAssessment,
  conjunction: ConjunctionSummary,
  signal: AbortSignal,
): Promise<ConjunctionVisualization> {
  return parseConjunctionVisualization(
    await json(
      await request(
        `/api/conjunction-assessments/${encodeURIComponent(selected.id)}/visualization`,
        signal,
      ),
    ),
    selected,
    conjunction,
  );
}
export async function createSimulation(
  key: string,
  signal: AbortSignal,
): Promise<PredictionRun> {
  const run = parsePredictionRun(
    await json(
      await request("/api/simulations", signal, {
        method: "POST",
        headers: { "Idempotency-Key": key },
      }),
    ),
  );
  if (run.source !== "Simulation") return invalid();
  return run;
}
