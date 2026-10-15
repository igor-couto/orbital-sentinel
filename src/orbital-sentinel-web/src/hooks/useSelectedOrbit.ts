import { useEffect, useState } from "react";
import { getOrbitSnapshot } from "../api/client";
import type { CatalogEntry, OrbitSnapshot } from "../domain/orbits";
import { pollResource } from "./pollResource";

interface SelectedOrbitState {
  objectId: string | undefined;
  orbitVersion: number | undefined;
  value: OrbitSnapshot | null;
  error: string | null;
}

/** Detailed elements never block catalog markers, and never cross object/revision boundaries. */
export function useSelectedOrbit(
  selected: CatalogEntry | undefined,
  revision: number,
) {
  const objectId = selected?.object.id;
  const orbitVersion = selected?.version;
  const [state, setState] = useState<SelectedOrbitState>({
    objectId: undefined,
    orbitVersion: undefined,
    value: null,
    error: null,
  });
  useEffect(() => {
    if (!objectId || orbitVersion === undefined) return;
    const controller = new AbortController();
    setState((previous) => ({
      objectId,
      orbitVersion,
      value:
        previous.value?.object.id === objectId &&
        previous.value.version >= orbitVersion
          ? previous.value
          : null,
      error: null,
    }));
    pollResource(
      async (signal) => {
        const value = await getOrbitSnapshot(objectId, signal);
        if (value.version < orbitVersion)
          throw new Error(
            "Orbital elements are updating. Retrying object details…",
          );
        return value;
      },
      60_000,
      controller.signal,
      (value) =>
        setState((previous) => ({
          objectId,
          orbitVersion,
          value:
            previous.value?.object.id === objectId &&
            previous.value.version > value.version
              ? previous.value
              : value,
          error: null,
        })),
      (error) =>
        setState((previous) => ({
          ...previous,
          error:
            error instanceof Error
              ? error.message
              : "Object details are unavailable.",
        })),
    );
    return () => controller.abort();
  }, [objectId, orbitVersion, revision]);
  const matches =
    state.objectId === objectId && state.orbitVersion === orbitVersion;
  const current =
    state.value &&
    state.value.object.id === objectId &&
    orbitVersion !== undefined &&
    state.value.version >= orbitVersion
      ? state.value
      : null;
  const error = matches ? state.error : null;
  return {
    value: current,
    error,
    loading: Boolean(objectId) && !current && !error,
  };
}
