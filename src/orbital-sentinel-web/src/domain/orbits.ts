import { hermiteWeights } from "./motion";

export interface OrbitalObject {
  id: string;
  noradCatalogId: number | null;
  name: string;
  internationalDesignator: string | null;
  objectType: string;
  updatedAt: string;
}

export interface OrbitalElements {
  epochUtc: string;
  meanMotionRevolutionsPerDay: number;
  eccentricity: number;
  inclinationDegrees: number;
  rightAscensionDegrees: number;
  argumentOfPericenterDegrees: number;
  meanAnomalyDegrees: number;
  bStar: number;
  meanMotionDot: number;
  meanMotionDdot: number;
}

export interface OrbitSnapshot {
  object: OrbitalObject;
  version: number;
  elements: OrbitalElements;
  fingerprint: string;
  ingestedAt: string;
}

export interface CatalogPage {
  items: OrbitSnapshot[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface EcefPosition {
  x: number;
  y: number;
  z: number;
}
export interface TrajectoryPoint {
  timestampUtc: string;
  positionEcefMeters: EcefPosition;
  altitudeKilometers?: number | null;
  speedKilometersPerSecond?: number | null;
  velocityEcefMetersPerSecond?: EcefPosition | null;
}
export interface ObjectPosition extends TrajectoryPoint {
  orbitalObjectId: string;
}
export interface Trajectory {
  orbitalObjectId: string;
  orbitVersion: number;
  startUtc: string;
  endUtc: string;
  stepSeconds: number;
  points: TrajectoryPoint[];
}

export type ObjectKind = "payload" | "rocket" | "debris" | "unknown";

export function objectKind(type: string): ObjectKind {
  const normalized = type.replace(/[\s_-]/g, "").toLowerCase();
  if (
    ["pay", "payload", "satellite", "spacecraft", "activesatellite"].includes(
      normalized,
    )
  )
    return "payload";
  if (["r/b", "rocketbody", "rocket"].includes(normalized)) return "rocket";
  if (["deb", "debris"].includes(normalized)) return "debris";
  return "unknown";
}

export const kindLabel: Record<ObjectKind, string> = {
  payload: "Payload",
  rocket: "Rocket body",
  debris: "Debris",
  unknown: "Unclassified",
};

export const kindColor: Record<ObjectKind, string> = {
  payload: "#76e3b6",
  rocket: "#a9a1ff",
  debris: "#efad70",
  unknown: "#9cabbf",
};

export function formatUtc(
  isoOrMillis: string | number,
  includeDate = false,
): string {
  return new Date(isoOrMillis).toLocaleString("en-GB", {
    timeZone: "UTC",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
    ...(includeDate ? { day: "2-digit", month: "short" } : {}),
  });
}

export function elementAge(
  epochUtc: string,
  now: number,
): { label: string; stale: boolean } {
  const age = (now - Date.parse(epochUtc)) / 3_600_000;
  if (age < 0) return { label: "Future epoch", stale: false };
  if (age < 1) return { label: `${Math.floor(age * 60)}m old`, stale: false };
  if (age < 48) return { label: `${Math.floor(age)}h old`, stale: false };
  return { label: `${Math.floor(age / 24)}d old`, stale: true };
}

/** Use backend velocity derivatives when present; older persisted paths remain linear. */
export function interpolatePosition(
  trajectory: Trajectory,
  timestamp: number,
): EcefPosition | undefined {
  const points = trajectory.points;
  if (
    !points.length ||
    timestamp < Date.parse(points[0].timestampUtc) ||
    timestamp > Date.parse(points[points.length - 1].timestampUtc)
  )
    return undefined;
  let low = 0;
  let high = points.length - 1;
  while (low < high) {
    const mid = Math.floor((low + high) / 2);
    if (Date.parse(points[mid].timestampUtc) < timestamp) low = mid + 1;
    else high = mid;
  }
  const right = points[low];
  if (low === 0 || Date.parse(right.timestampUtc) === timestamp)
    return right.positionEcefMeters;
  const left = points[low - 1];
  const fraction =
    (timestamp - Date.parse(left.timestampUtc)) /
    (Date.parse(right.timestampUtc) - Date.parse(left.timestampUtc));
  if (left.velocityEcefMetersPerSecond && right.velocityEcefMetersPerSecond) {
    const w = hermiteWeights(
      fraction,
      (Date.parse(right.timestampUtc) - Date.parse(left.timestampUtc)) / 1000,
    );
    const a = left.positionEcefMeters,
      b = right.positionEcefMeters;
    const va = left.velocityEcefMetersPerSecond,
      vb = right.velocityEcefMetersPerSecond;
    return {
      x: w[0] * a.x + w[1] * va.x + w[2] * b.x + w[3] * vb.x,
      y: w[0] * a.y + w[1] * va.y + w[2] * b.y + w[3] * vb.y,
      z: w[0] * a.z + w[1] * va.z + w[2] * b.z + w[3] * vb.z,
    };
  }
  return {
    x:
      left.positionEcefMeters.x +
      fraction * (right.positionEcefMeters.x - left.positionEcefMeters.x),
    y:
      left.positionEcefMeters.y +
      fraction * (right.positionEcefMeters.y - left.positionEcefMeters.y),
    z:
      left.positionEcefMeters.z +
      fraction * (right.positionEcefMeters.z - left.positionEcefMeters.z),
  };
}

export function isCurrentPosition(
  position: ObjectPosition,
  now: number,
): boolean {
  return Math.abs(Date.parse(position.timestampUtc) - now) <= 120_000;
}
