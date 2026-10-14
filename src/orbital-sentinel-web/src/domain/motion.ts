import type { EcefPosition } from "./orbits";

export interface ObjectMotion {
  orbitalObjectId: string;
  orbitVersion: number;
  states: Float64Array;
}
export interface CatalogMotion {
  id: string;
  startUtc: string;
  endUtc: string;
  start: number;
  end: number;
  stepSeconds: number;
  catalogCount: number;
  objects: ObjectMotion[];
}

export function motionCovers(
  motion: CatalogMotion | null,
  time: number,
): boolean {
  return !!motion && time >= motion.start && time <= motion.end;
}

/** Cubic Hermite interpolation of backend positions and their actual ECEF derivatives. */
export function hermiteWeights(fraction: number, seconds: number) {
  const t2 = fraction * fraction;
  const t3 = t2 * fraction;
  return [
    2 * t3 - 3 * t2 + 1,
    (t3 - 2 * t2 + fraction) * seconds,
    -2 * t3 + 3 * t2,
    (t3 - t2) * seconds,
  ];
}

export function motionInterval(motion: CatalogMotion, time: number) {
  if (!motionCovers(motion, time)) return null;
  const interval = (time - motion.start) / (motion.stepSeconds * 1000);
  const index = Math.min(
    Math.floor(interval),
    Math.round((motion.end - motion.start) / (motion.stepSeconds * 1000)) - 1,
  );
  return {
    offset: index * 6,
    weights: hermiteWeights(interval - index, motion.stepSeconds),
  };
}

export function sampleMotion(
  motion: CatalogMotion,
  object: ObjectMotion,
  time: number,
  result: EcefPosition,
): EcefPosition | undefined {
  const interval = motionInterval(motion, time);
  if (!interval) return undefined;
  const { offset: i, weights: w } = interval;
  const s = object.states;
  result.x = w[0] * s[i] + w[1] * s[i + 3] + w[2] * s[i + 6] + w[3] * s[i + 9];
  result.y =
    w[0] * s[i + 1] + w[1] * s[i + 4] + w[2] * s[i + 7] + w[3] * s[i + 10];
  result.z =
    w[0] * s[i + 2] + w[1] * s[i + 5] + w[2] * s[i + 8] + w[3] * s[i + 11];
  return result;
}
