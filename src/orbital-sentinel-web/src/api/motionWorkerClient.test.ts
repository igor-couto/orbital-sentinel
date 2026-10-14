import { afterEach, expect, it, vi } from "vitest";
import { createMotionWorkerClient } from "./motionWorkerClient";
import type { CatalogMotion } from "../domain/motion";

class FakeWorker {
  static instances: FakeWorker[] = [];
  postMessage = vi.fn();
  terminate = vi.fn();
  onmessage?: (event: { data: unknown }) => void;
  onerror?: () => void;
  constructor() {
    FakeWorker.instances.push(this);
  }
  reply(data: unknown) {
    this.onmessage?.({ data });
  }
}
function setup() {
  FakeWorker.instances = [];
  vi.stubGlobal("Worker", FakeWorker);
  return createMotionWorkerClient();
}
const buffer = { id: "current", objects: [] } as unknown as CatalogMotion;
afterEach(() => vi.unstubAllGlobals());

it("reuses the displayed buffer when the worker reports it unchanged", async () => {
  const client = setup(),
    signal = new AbortController().signal;
  const initial = client.load(signal),
    worker = FakeWorker.instances[0];
  worker.reply({ id: 1, value: buffer });
  expect(await initial).toBe(buffer);
  const next = client.load(signal);
  expect(worker.postMessage).toHaveBeenLastCalledWith({
    id: 2,
    knownId: "current",
  });
  worker.reply({ id: 2, value: null });
  expect(await next).toBe(buffer);
  client.dispose();
});

it("cancels obsolete work and cannot replace the buffer with a late response", async () => {
  const client = setup(),
    obsolete = new AbortController();
  const initial = client.load(obsolete.signal),
    worker = FakeWorker.instances[0];
  const rejected = expect(initial).rejects.toMatchObject({
    name: "AbortError",
  });
  obsolete.abort();
  await rejected;
  expect(worker.postMessage).toHaveBeenLastCalledWith({ id: 1, cancel: true });
  const next = client.load(new AbortController().signal);
  worker.reply({ id: 2, value: buffer });
  expect(await next).toBe(buffer);
  worker.reply({ id: 1, value: { ...buffer, id: "obsolete" } });
  const unchanged = client.load(new AbortController().signal);
  worker.reply({ id: 3, value: null });
  expect(await unchanged).toBe(buffer);
  client.dispose();
});

it("retains usable data after a worker failure and restarts on the next poll", async () => {
  const client = setup(),
    signal = new AbortController().signal;
  const initial = client.load(signal),
    worker = FakeWorker.instances[0];
  worker.reply({ id: 1, value: buffer });
  await initial;
  const failed = client.load(signal);
  const rejected = expect(failed).rejects.toThrow(
    "Unable to prepare motion data",
  );
  worker.onerror?.();
  await rejected;
  expect(worker.terminate).toHaveBeenCalledOnce();
  const retry = client.load(signal),
    restarted = FakeWorker.instances[1];
  expect(restarted.postMessage).toHaveBeenLastCalledWith({
    id: 3,
    knownId: "current",
  });
  restarted.reply({ id: 3, value: null });
  expect(await retry).toBe(buffer);
  client.dispose();
});

it("releases worker resources and rejects pending requests when unmounted", async () => {
  const client = setup();
  const pending = client.load(new AbortController().signal);
  const rejected = expect(pending).rejects.toThrow("canceled");
  client.dispose();
  await rejected;
  expect(FakeWorker.instances[0].terminate).toHaveBeenCalledOnce();
});
