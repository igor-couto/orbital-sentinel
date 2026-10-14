import type {
  CatalogPage,
  ObjectPosition,
  OrbitSnapshot,
  Trajectory,
} from "../domain/orbits";

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
  ) {
    super(message);
  }
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null;
const isDate = (value: unknown): value is string =>
  typeof value === "string" && Number.isFinite(Date.parse(value));
const isNumber = (value: unknown): value is number =>
  typeof value === "number" && Number.isFinite(value);
const isInteger = (value: unknown): value is number =>
  isNumber(value) && Number.isSafeInteger(value);
const invalid = (): never => {
  throw new ApiError(
    "The service returned an invalid orbital-data response.",
    502,
  );
};

function validPoint(value: unknown): boolean {
  if (
    !isRecord(value) ||
    !isDate(value.timestampUtc) ||
    !isRecord(value.positionEcefMeters)
  )
    return false;
  const position = value.positionEcefMeters;
  return (
    ["x", "y", "z"].every((axis) => isNumber(position[axis])) &&
    ["altitudeKilometers", "speedKilometersPerSecond"].every(
      (key) =>
        value[key] === undefined || value[key] === null || isNumber(value[key]),
    ) &&
    (value.speedKilometersPerSecond == null ||
      (value.speedKilometersPerSecond as number) >= 0)
  );
}

export function parseCatalog(value: unknown): CatalogPage {
  if (
    !isRecord(value) ||
    !Array.isArray(value.items) ||
    !isInteger(value.totalCount) ||
    value.totalCount < 0 ||
    !isInteger(value.page) ||
    value.page < 1 ||
    !isInteger(value.pageSize) ||
    value.pageSize < 1
  )
    return invalid();
  for (const item of value.items) {
    if (
      !isRecord(item) ||
      !isRecord(item.object) ||
      !isRecord(item.elements) ||
      typeof item.object.id !== "string" ||
      !item.object.id ||
      typeof item.object.name !== "string" ||
      typeof item.object.objectType !== "string" ||
      !isDate(item.elements.epochUtc) ||
      !isInteger(item.version) ||
      item.version < 1 ||
      !isDate(item.object.updatedAt) ||
      !isDate(item.ingestedAt) ||
      typeof item.fingerprint !== "string" ||
      !(
        item.object.internationalDesignator === null ||
        typeof item.object.internationalDesignator === "string"
      ) ||
      !(
        item.object.noradCatalogId === null ||
        isInteger(item.object.noradCatalogId)
      )
    )
      return invalid();
    const elements = item.elements;
    if (
      ![
        "meanMotionRevolutionsPerDay",
        "eccentricity",
        "inclinationDegrees",
        "rightAscensionDegrees",
        "argumentOfPericenterDegrees",
        "meanAnomalyDegrees",
        "bStar",
        "meanMotionDot",
        "meanMotionDdot",
      ].every((key) => isNumber(elements[key])) ||
      (elements.meanMotionRevolutionsPerDay as number) <= 0 ||
      (elements.eccentricity as number) < 0 ||
      (elements.eccentricity as number) >= 1
    )
      return invalid();
  }
  return value as unknown as CatalogPage;
}

export function parsePositions(value: unknown): ObjectPosition[] {
  if (
    !Array.isArray(value) ||
    !value.every(
      (point) => validPoint(point) && typeof point.orbitalObjectId === "string",
    )
  )
    return invalid();
  return value as ObjectPosition[];
}

export function parseTrajectory(value: unknown, objectId: string): Trajectory {
  if (
    !isRecord(value) ||
    value.orbitalObjectId !== objectId ||
    !isInteger(value.orbitVersion) ||
    !isDate(value.startUtc) ||
    !isDate(value.endUtc) ||
    !isNumber(value.stepSeconds) ||
    value.stepSeconds <= 0 ||
    !Array.isArray(value.points) ||
    value.points.length < 2 ||
    !value.points.every(validPoint)
  )
    return invalid();
  const points = value.points as Trajectory["points"];
  if (
    Date.parse(value.startUtc) !== Date.parse(points[0].timestampUtc) ||
    Date.parse(value.endUtc) !==
      Date.parse(points[points.length - 1].timestampUtc) ||
    points.some(
      (point, index) =>
        index > 0 &&
        Date.parse(point.timestampUtc) <=
          Date.parse(points[index - 1].timestampUtc),
    )
  )
    return invalid();
  return value as unknown as Trajectory;
}

export async function request(
  path: string,
  signal: AbortSignal,
  options: RequestInit = {},
): Promise<Response> {
  let response: Response;
  try {
    response = await fetch(path, {
      ...options,
      signal: AbortSignal.any([signal, AbortSignal.timeout(12_000)]),
      headers: { Accept: "application/json", ...options.headers },
    });
  } catch (error) {
    if (signal.aborted) throw error;
    throw new ApiError(
      "Cannot reach the orbital-data service. Check that the backend is running.",
      0,
    );
  }
  if (!response.ok) {
    let title: string | undefined;
    try {
      const problem: unknown = await response.json();
      if (isRecord(problem) && typeof problem.title === "string")
        title = problem.title.slice(0, 180);
    } catch {
      /* Non-JSON failures still get a useful HTTP-status message. */
    }
    throw new ApiError(
      title ?? `The orbital-data service returned HTTP ${response.status}.`,
      response.status,
    );
  }
  return response;
}

async function readJson(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return invalid();
  }
}

export async function getCatalog(signal: AbortSignal): Promise<CatalogPage> {
  const loadPage = async (page: number, pageSize: number) => {
    const response = await request(
      `/api/orbital-objects?page=${page}&pageSize=${pageSize}`,
      signal,
    );
    const result = parseCatalog(await readJson(response));
    if (result.page !== page || result.items.length > result.pageSize)
      return invalid();
    return result;
  };
  const first = await loadPage(1, 500);
  const objects = new Map<string, OrbitSnapshot>();
  for (const item of first.items) objects.set(item.object.id, item);
  // Keep HTTP pages bounded, but load the whole catalog. Pin the page count for
  // this refresh so ingestion cannot extend it indefinitely while we fetch.
  const pages = Math.ceil(first.totalCount / first.pageSize);
  let totalCount = first.totalCount;
  for (let page = 2; page <= pages; page++) {
    const next = await loadPage(page, first.pageSize);
    if (next.pageSize !== first.pageSize) return invalid();
    for (const item of next.items) objects.set(item.object.id, item);
    totalCount = next.totalCount;
  }
  // Updates between pages can move an object across a page boundary. Deduplicate
  // its identity; the next scheduled refresh picks up concurrent additions.
  return {
    ...first,
    totalCount: Math.max(totalCount, objects.size),
    items: [...objects.values()],
  };
}

export async function getPositions(
  signal: AbortSignal,
): Promise<ObjectPosition[]> {
  const response = await request("/api/orbital-objects/positions", signal);
  return parsePositions(await readJson(response));
}

export type TrajectoryResult =
  | { status: "ready"; trajectory: Trajectory }
  | { status: "pending"; retryAfterMs: number };

export async function getTrajectory(
  objectId: string,
  signal: AbortSignal,
): Promise<TrajectoryResult> {
  const response = await request(
    `/api/orbital-objects/${encodeURIComponent(objectId)}/trajectory`,
    signal,
  );
  if (response.status === 202) {
    const seconds = Number(response.headers.get("Retry-After") ?? "10");
    return {
      status: "pending",
      retryAfterMs: Number.isFinite(seconds)
        ? Math.max(5, Math.min(60, seconds)) * 1000
        : 10_000,
    };
  }
  return {
    status: "ready",
    trajectory: parseTrajectory(await readJson(response), objectId),
  };
}
