import { interpolatePosition, trajectoryInterval } from "./orbits";
import type { EcefPosition, PathView, Trajectory } from "./orbits";

/** One orbital period ahead, bounded only by the available prediction samples. */
export function predictionPath(
  trajectory: Trajectory,
  timestamp: number,
  periodMinutes: number,
  view: PathView = "earth-relative",
): EcefPosition[] {
  if (!Number.isFinite(periodMinutes) || periodMinutes <= 0) return [];
  const end = Math.min(
    timestamp + periodMinutes * 60_000,
    Date.parse(trajectory.endUtc),
  );
  const first = interpolatePosition(trajectory, timestamp, view);
  const last = interpolatePosition(trajectory, end, view);
  if (!first || !last || end <= timestamp) return [];

  const positions = [first];
  for (const point of trajectory.points) {
    const time = Date.parse(point.timestampUtc);
    if (time > timestamp && time < end) {
      const p =
        view === "orbit"
          ? point.orbitView?.positionMeters
          : point.positionEcefMeters;
      if (!p) return [];
      positions.push(p);
    }
  }
  // Include the exact period/window endpoint even between sampling intervals.
  // Earth rotates during this trace: its endpoints must not be joined into a loop.
  positions.push(last);
  if (view === "orbit") {
    const interval = trajectoryInterval(trajectory, timestamp)!;
    const a = interval.left.orbitView?.rotationToEarthFixedRadians;
    const b = interval.right.orbitView?.rotationToEarthFixedRadians;
    if (a == null || b == null) return [];
    // A single display rotation of backend-prepared geometry, using the backend's
    // unwrapped orientation samples. No sidereal-time or orbital model in the browser.
    const angle = a + interval.fraction * (b - a);
    const cos = Math.cos(angle),
      sin = Math.sin(angle);
    return positions.map((p) => ({
      x: cos * p.x - sin * p.y,
      y: sin * p.x + cos * p.y,
      z: p.z,
    }));
  }
  return positions;
}
