import { ApiError, request } from "./client";
import type { ObjectDetails } from "../domain/objectDetails";

export function parseObjectDetails(
  value: unknown,
  objectId: string,
): ObjectDetails {
  const invalid = () => {
    throw new ApiError("The service returned invalid object details.", 502);
  };
  if (!value || typeof value !== "object") return invalid();
  const data = value as ObjectDetails;
  if (
    data.orbitalObjectId !== objectId ||
    !Number.isSafeInteger(data.orbitVersion) ||
    data.orbitVersion < 1 ||
    typeof data.elementsSource !== "string" ||
    !data.meanOrbit ||
    ![
      data.meanOrbit.periodMinutes,
      data.meanOrbit.perigeeKilometers,
      data.meanOrbit.apogeeKilometers,
    ].every((x) => typeof x === "number" && Number.isFinite(x)) ||
    data.meanOrbit.periodMinutes <= 0 ||
    data.meanOrbit.perigeeKilometers > data.meanOrbit.apogeeKilometers
  )
    return invalid();
  if (data.metadata !== null) {
    const meta = data.metadata;
    if (
      !meta ||
      ![
        meta.objectType,
        meta.ownerCode,
        meta.launchSiteCode,
        meta.operationalStatusCode,
        meta.launchDate,
      ].every((x) => x === null || typeof x === "string") ||
      !Array.isArray(meta.groups) ||
      !meta.groups.every((x) => typeof x === "string") ||
      !Number.isFinite(Date.parse(meta.retrievedAtUtc)) ||
      (meta.launchDate !== null &&
        !Number.isFinite(Date.parse(meta.launchDate)))
    )
      return invalid();
  }
  return data;
}

export async function getObjectDetails(objectId: string, signal: AbortSignal) {
  const response = await request(
    `/api/orbital-objects/${encodeURIComponent(objectId)}/details`,
    signal,
  );
  return parseObjectDetails(await response.json(), objectId);
}
