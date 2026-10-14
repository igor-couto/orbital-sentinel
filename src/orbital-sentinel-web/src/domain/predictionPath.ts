import { interpolatePosition } from "./orbits";
import type { EcefPosition, Trajectory } from "./orbits";

/** One orbital period ahead, bounded only by the available prediction samples. */
export function predictionPath(
  trajectory: Trajectory,
  timestamp: number,
  periodMinutes: number,
): EcefPosition[] {
  if (!Number.isFinite(periodMinutes) || periodMinutes <= 0) return [];
  const end = Math.min(
    timestamp + periodMinutes * 60_000,
    Date.parse(trajectory.endUtc),
  );
  const first = interpolatePosition(trajectory, timestamp);
  const last = interpolatePosition(trajectory, end);
  if (!first || !last || end <= timestamp) return [];

  const positions = [first];
  for (const point of trajectory.points) {
    const time = Date.parse(point.timestampUtc);
    if (time > timestamp && time < end)
      positions.push(point.positionEcefMeters);
  }
  // Include the exact period/window endpoint even between sampling intervals.
  // Earth rotates during this trace: its endpoints must not be joined into a loop.
  positions.push(last);
  return positions;
}
