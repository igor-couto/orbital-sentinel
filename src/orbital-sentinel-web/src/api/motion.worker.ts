import { createMotionLoader } from "./client";

const load = createMotionLoader();
const requests = new Map<number, AbortController>();

// Network decoding and validation stay off the rendering thread. This worker
// performs no propagation, coordinate conversion, or other orbital calculations.
self.onmessage = async (
  event: MessageEvent<{ id: number; knownId?: string; cancel?: boolean }>,
) => {
  const { id, knownId, cancel } = event.data;
  if (cancel) {
    requests.get(id)?.abort();
    return;
  }
  const controller = new AbortController();
  requests.set(id, controller);
  try {
    const value = await load(controller.signal);
    if (!controller.signal.aborted)
      self.postMessage({ id, value: value.id === knownId ? null : value });
  } catch (error) {
    if (!controller.signal.aborted)
      self.postMessage({
        id,
        error:
          error instanceof Error
            ? error.message
            : "Unable to load motion data.",
      });
  } finally {
    requests.delete(id);
  }
};
