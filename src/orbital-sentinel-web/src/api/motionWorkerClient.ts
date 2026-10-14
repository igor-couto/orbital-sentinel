import type { CatalogMotion } from "../domain/motion";

export function createMotionWorkerClient() {
  let worker: Worker | null = null;
  let cached: CatalogMotion | null = null;
  let sequence = 0;
  const pending = new Map<
    number,
    {
      resolve: (value: CatalogMotion) => void;
      reject: (reason: Error) => void;
      cleanup: () => void;
    }
  >();

  function dispose(reason = new Error("Motion request canceled.")) {
    worker?.terminate();
    worker = null;
    for (const request of pending.values()) {
      request.cleanup();
      request.reject(reason);
    }
    pending.clear();
  }

  function load(signal: AbortSignal): Promise<CatalogMotion> {
    if (signal.aborted)
      return Promise.reject(new DOMException("Aborted", "AbortError"));
    if (!worker) {
      worker = new Worker(new URL("./motion.worker.ts", import.meta.url), {
        type: "module",
      });
      worker.onmessage = (
        event: MessageEvent<{
          id: number;
          value?: CatalogMotion | null;
          error?: string;
        }>,
      ) => {
        const request = pending.get(event.data.id);
        if (!request) return;
        pending.delete(event.data.id);
        request.cleanup();
        if (event.data.error) request.reject(new Error(event.data.error));
        else {
          cached = event.data.value ?? cached;
          if (cached) request.resolve(cached);
          else request.reject(new Error("Motion data is unavailable."));
        }
      };
      worker.onerror = () =>
        dispose(
          new Error("Unable to prepare motion data. Refresh to try again."),
        );
      worker.onmessageerror = () =>
        dispose(new Error("Unable to read motion data."));
    }
    const id = ++sequence;
    return new Promise((resolve, reject) => {
      const abort = () => {
        pending.delete(id);
        worker?.postMessage({ id, cancel: true });
        signal.removeEventListener("abort", abort);
        reject(new DOMException("Aborted", "AbortError"));
      };
      pending.set(id, {
        resolve,
        reject,
        cleanup: () => signal.removeEventListener("abort", abort),
      });
      signal.addEventListener("abort", abort, { once: true });
      worker!.postMessage({ id, knownId: cached?.id });
    });
  }
  return { load, dispose };
}
