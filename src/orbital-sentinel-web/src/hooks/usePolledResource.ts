import { useEffect, useState } from "react";
import { pollResource } from "./pollResource";

export interface Resource<T> {
  value: T | null;
  error: string | null;
  loading: boolean;
  receivedAt: number | null;
}

export function usePolledResource<T>(
  load: (signal: AbortSignal) => Promise<T>,
  intervalMs: number,
  revision: number,
): Resource<T> {
  const [resource, setResource] = useState<Resource<T>>({
    value: null,
    error: null,
    loading: true,
    receivedAt: null,
  });
  useEffect(() => {
    const controller = new AbortController();
    pollResource(
      load,
      intervalMs,
      controller.signal,
      (value) =>
        setResource({
          value,
          error: null,
          loading: false,
          receivedAt: Date.now(),
        }),
      (error) =>
        setResource((previous) => ({
          ...previous,
          loading: false,
          error:
            error instanceof Error
              ? error.message
              : "Unable to load orbital data.",
        })),
    );
    return () => controller.abort();
  }, [load, intervalMs, revision]);
  return resource;
}
