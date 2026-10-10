import type { EcefPosition, OrbitSnapshot, Trajectory } from "./orbits";

export type PredictionSource = "Real" | "Simulation";
export interface PredictionRun {
  id: string;
  source: PredictionSource;
  status: "Pending" | "Running" | "Completed" | "Failed";
  createdAtUtc: string;
  predictionStartUtc: string;
  predictionEndUtc: string;
  clockUtc: string;
  objectCount: number;
  error?: string | null;
}
export interface ConjunctionAssessment {
  id: string;
  predictionRunId: string;
  timeOfClosestApproachUtc: string;
  missDistanceMeters: number;
  relativeVelocityMetersPerSecond: number;
  detectedAtUtc: string;
  primaryOrbitVersion: number;
  secondaryOrbitVersion: number;
  isWindowBoundary: boolean;
  isExtendedProximity: boolean;
}
export interface ConjunctionSummary {
  id: string;
  source: PredictionSource;
  primaryObjectId: string;
  secondaryObjectId: string;
  primaryName: string;
  secondaryName: string;
  latestAssessment: ConjunctionAssessment;
}
export interface ConjunctionDetail extends ConjunctionSummary {
  assessments: ConjunctionAssessment[];
}
export interface ConjunctionPage {
  items: ConjunctionSummary[];
  page: number;
  pageSize: number;
  totalCount: number;
}
export interface ConjunctionVisualization {
  assessmentId: string;
  predictionRunId: string;
  source: PredictionSource;
  timeOfClosestApproachUtc: string;
  primary: OrbitSnapshot;
  secondary: OrbitSnapshot;
  primaryTrajectory: Trajectory;
  secondaryTrajectory: Trajectory;
  primaryPositionEcefMeters: EcefPosition;
  secondaryPositionEcefMeters: EcefPosition;
}

/** Simulation time is explicit and fixed; wall-clock time never ages a fixture. */
export function predictionClock(
  run: PredictionRun | null,
  now: number,
): number {
  return run?.source === "Simulation" ? Date.parse(run.clockUtc) : now;
}

export function tcaCountdown(tcaUtc: string, clock: number): string {
  const delta = Date.parse(tcaUtc) - clock;
  if (Math.abs(delta) < 1000) return "At closest approach";
  const seconds = Math.floor(Math.abs(delta) / 1000);
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const remaining = seconds % 60;
  const duration = hours
    ? `${hours}h ${minutes}m`
    : minutes
      ? `${minutes}m ${remaining}s`
      : `${remaining}s`;
  return delta < 0 ? `${duration} ago` : `In ${duration}`;
}

export function formatDistance(meters: number): string {
  return meters < 1000
    ? `${meters.toFixed(1)} m`
    : `${(meters / 1000).toFixed(2)} km`;
}
export function formatSpeed(metersPerSecond: number): string {
  return metersPerSecond < 1000
    ? `${metersPerSecond.toFixed(1)} m/s`
    : `${(metersPerSecond / 1000).toFixed(2)} km/s`;
}

export function completedWindow(
  run: PredictionRun | null,
  now: number,
): boolean {
  return (
    run?.status === "Completed" &&
    Date.parse(run.predictionEndUtc) > predictionClock(run, now)
  );
}
