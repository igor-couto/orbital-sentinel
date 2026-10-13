import type { Trajectory } from "./orbits";

export interface ObjectDetails {
  orbitalObjectId: string;
  orbitVersion: number;
  meanOrbit: {
    periodMinutes: number;
    perigeeKilometers: number;
    apogeeKilometers: number;
  };
  elementsSource: string;
  metadata: {
    objectType: string | null;
    ownerCode: string | null;
    launchDate: string | null;
    launchSiteCode: string | null;
    operationalStatusCode: string | null;
    groups: string[];
    retrievedAtUtc: string;
  } | null;
}

/** Interpolate scalar SGP4 outputs, not speed inferred from adjacent positions. */
export function interpolateFlightMetrics(
  trajectory: Trajectory,
  timestamp: number,
) {
  const points = trajectory.points;
  if (
    !Number.isFinite(timestamp) ||
    !points.length ||
    timestamp < Date.parse(points[0].timestampUtc) ||
    timestamp > Date.parse(points.at(-1)!.timestampUtc)
  )
    return null;
  let rightIndex = points.findIndex(
    (point) => Date.parse(point.timestampUtc) >= timestamp,
  );
  if (rightIndex < 0) return null;
  const right = points[rightIndex];
  const left =
    rightIndex === 0 || Date.parse(right.timestampUtc) === timestamp
      ? right
      : points[rightIndex - 1];
  const fraction =
    left === right
      ? 0
      : (timestamp - Date.parse(left.timestampUtc)) /
        (Date.parse(right.timestampUtc) - Date.parse(left.timestampUtc));
  const interpolate = (
    a: number | null | undefined,
    b: number | null | undefined,
  ) =>
    typeof a === "number" &&
    typeof b === "number" &&
    Number.isFinite(a) &&
    Number.isFinite(b)
      ? a + fraction * (b - a)
      : null;
  return {
    altitudeKilometers: interpolate(
      left.altitudeKilometers,
      right.altitudeKilometers,
    ),
    speedKilometersPerSecond: interpolate(
      left.speedKilometersPerSecond,
      right.speedKilometersPerSecond,
    ),
  };
}

export const operationalStatus: Record<string, string> = {
  "+": "Operational",
  "-": "Nonoperational",
  P: "Partially operational",
  B: "Backup / standby",
  S: "Spare",
  X: "Extended mission",
  D: "Decayed / deorbited",
  "?": "Unknown",
};

export const groupLabels: Record<string, string> = {
  stations: "Space stations",
  visual: "Brightest objects",
  "iridium-33-debris": "Iridium 33 debris",
  "cosmos-2251-debris": "Cosmos 2251 debris",
  "fengyun-1c-debris": "Fengyun 1C debris",
  active: "Active satellites",
  starlink: "Starlink",
  oneweb: "OneWeb",
  science: "Space and Earth science",
  "iridium-NEXT": "Iridium NEXT",
  weather: "Weather",
  resource: "Earth resources",
};

// Group membership identifies these constellation operators; object names alone do not.
// References: https://www.starlink.com/technology and https://oneweb.net/
export function constellationOperator(groups: string[], objectType: string) {
  if (!["PAY", "PAYLOAD"].includes(objectType.toUpperCase())) return null;
  if (groups.includes("starlink")) return "SpaceX";
  if (groups.includes("oneweb")) return "Eutelsat OneWeb";
  return null;
}
