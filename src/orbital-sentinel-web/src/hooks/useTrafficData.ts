import { useCallback, useEffect, useRef, useState } from "react";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { getCatalog, getPositions, getTrajectory } from "../api/client";
import type {
  CatalogPage,
  ObjectPosition,
  OrbitSnapshot,
  Trajectory,
} from "../domain/orbits";

export type ConnectionState =
  "connecting" | "live" | "reconnecting" | "polling";
interface Resource<T> {
  value: T | null;
  error: string | null;
  loading: boolean;
  receivedAt: number | null;
}
const emptyResource = <T>(): Resource<T> => ({
  value: null,
  error: null,
  loading: true,
  receivedAt: null,
});

function usePolledResource<T>(
  load: (signal: AbortSignal) => Promise<T>,
  intervalMs: number,
  revision: number,
): Resource<T> {
  const [resource, setResource] = useState<Resource<T>>(emptyResource);
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function refresh() {
      try {
        const value = await load(controller.signal);
        if (!controller.signal.aborted)
          setResource({
            value,
            error: null,
            loading: false,
            receivedAt: Date.now(),
          });
      } catch (error) {
        if (!controller.signal.aborted)
          setResource((previous) => ({
            ...previous,
            loading: false,
            error:
              error instanceof Error
                ? error.message
                : "Unable to load orbital data.",
          }));
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(refresh, intervalMs);
      }
    }
    void refresh();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [load, intervalMs, revision]);
  return resource;
}

export function useTrafficData() {
  const [revision, setRevision] = useState(0);
  const [manualRevision, setManualRevision] = useState(0);
  const [connection, setConnection] = useState<ConnectionState>("connecting");
  const [trajectoryUpdates, setTrajectoryUpdates] = useState<
    Record<string, number>
  >({});
  const invalidate = useCallback(
    () => setRevision((previous) => previous + 1),
    [],
  );
  const refresh = useCallback(() => {
    setRevision((previous) => previous + 1);
    setManualRevision((previous) => previous + 1);
  }, []);
  const catalog = usePolledResource<CatalogPage>(getCatalog, 300_000, revision);
  const positions = usePolledResource<ObjectPosition[]>(
    getPositions,
    15_000,
    revision,
  );
  const pendingUpdates = useRef(new Set<string>());

  useEffect(() => {
    let disposed = false;
    let debounce: ReturnType<typeof setTimeout> | undefined;
    const hub = new HubConnectionBuilder()
      .withUrl("/hubs/traffic")
      .withAutomaticReconnect([0, 2000, 10_000, 30_000])
      .configureLogging({
        log(level, message) {
          // React development StrictMode intentionally cancels the first setup.
          // Do not report that expected cancellation as a connection failure.
          if (!disposed && level >= LogLevel.Warning) console.warn(message);
        },
      })
      .build();
    hub.on("TrajectoryAvailable", (message: unknown) => {
      if (
        typeof message !== "object" ||
        message === null ||
        !("orbitalObjectId" in message) ||
        typeof message.orbitalObjectId !== "string"
      )
        return;
      pendingUpdates.current.add(message.orbitalObjectId);
      if (debounce) return;
      debounce = setTimeout(() => {
        if (disposed) return;
        const updated = [...pendingUpdates.current];
        pendingUpdates.current.clear();
        setTrajectoryUpdates((previous) => {
          const next = { ...previous };
          for (const id of updated) next[id] = (next[id] ?? 0) + 1;
          return next;
        });
        invalidate();
        debounce = undefined;
      }, 1000);
    });
    hub.onreconnecting(() => {
      if (!disposed) setConnection("reconnecting");
    });
    hub.onreconnected(() => {
      if (!disposed) {
        setConnection("live");
        invalidate();
      }
    });
    hub.onclose(() => {
      if (!disposed) setConnection("polling");
    });
    void hub
      .start()
      .then(() => {
        if (!disposed) setConnection("live");
      })
      .catch(() => {
        if (!disposed) setConnection("polling");
      });
    return () => {
      disposed = true;
      clearTimeout(debounce);
      pendingUpdates.current.clear();
      void hub.stop();
    };
  }, [invalidate]);

  return {
    catalog,
    positions,
    connection,
    trajectoryUpdates,
    refresh,
    revision: manualRevision,
  };
}

export interface TrajectoryState {
  objectId: string | null;
  value: Trajectory | null;
  loading: boolean;
  pending: boolean;
  error: string | null;
}
export function useSelectedTrajectory(
  selected: OrbitSnapshot | undefined,
  updateVersion: number,
  revision: number,
): TrajectoryState {
  const objectId = selected?.object.id;
  const orbitVersion = selected?.version;
  const [state, setState] = useState<TrajectoryState>({
    objectId: null,
    value: null,
    loading: false,
    pending: false,
    error: null,
  });
  useEffect(() => {
    if (!objectId) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    // Refresh the same orbital revision in the background. Clearing valid samples
    // here makes both the selected marker and path disappear during every reload.
    const retainedValue = (previous: TrajectoryState) =>
      previous.objectId === objectId &&
      previous.value?.orbitVersion === orbitVersion
        ? previous.value
        : null;
    setState((previous) => ({
      objectId,
      value: retainedValue(previous),
      loading: true,
      pending: false,
      error: null,
    }));
    async function refresh() {
      let retryAfterMs = 60_000;
      try {
        const result = await getTrajectory(objectId!, controller.signal);
        if (controller.signal.aborted) return;
        if (
          result.status === "pending" ||
          result.trajectory.orbitVersion !== orbitVersion
        ) {
          retryAfterMs =
            result.status === "pending" ? result.retryAfterMs : 10_000;
          setState((previous) => ({
            objectId: objectId!,
            value: retainedValue(previous),
            loading: false,
            pending: true,
            error: null,
          }));
        } else {
          setState({
            objectId: objectId!,
            value: result.trajectory,
            loading: false,
            pending: false,
            error: null,
          });
        }
      } catch (error) {
        if (!controller.signal.aborted)
          setState((previous) => ({
            objectId: objectId!,
            value: retainedValue(previous),
            loading: false,
            pending: false,
            error:
              error instanceof Error
                ? error.message
                : "Unable to load this prediction.",
          }));
      } finally {
        if (!controller.signal.aborted)
          timer = setTimeout(refresh, retryAfterMs);
      }
    }
    void refresh();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [objectId, orbitVersion, updateVersion, revision]);
  return state.objectId === objectId &&
    (!state.value || state.value.orbitVersion === orbitVersion)
    ? state
    : {
        objectId: objectId ?? null,
        value: null,
        loading: Boolean(objectId),
        pending: false,
        error: null,
      };
}
