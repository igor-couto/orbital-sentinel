import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type * as Cesium from "cesium";

class Resource {
  src = "";
  href = "";
  rel = "";
  async = false;
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  remove = vi.fn();
  constructor(readonly tag: string) {}
}

const cesium = { Viewer: class {} } as unknown as typeof Cesium;
let resources: Resource[];

beforeEach(() => {
  vi.resetModules();
  resources = [];
  vi.stubGlobal("window", {});
  vi.stubGlobal("document", {
    createElement: (tag: string) => new Resource(tag),
    head: { appendChild: (resource: Resource) => resources.push(resource) },
  });
});
afterEach(() => vi.unstubAllGlobals());

it("shares one asynchronous load and waits for both the engine and its styles", async () => {
  const { loadCesium } = await import("./cesiumLoader");
  const first = loadCesium();
  expect(loadCesium()).toBe(first);
  const [script, stylesheet] = resources;
  expect(resources).toHaveLength(2);
  expect(script).toMatchObject({
    tag: "script",
    src: "/cesium/Cesium.js",
    async: true,
  });
  expect(stylesheet).toMatchObject({
    tag: "link",
    rel: "stylesheet",
    href: "/cesium/Widgets/widgets.css",
  });
  expect(window.CESIUM_BASE_URL).toBe("/cesium/");

  let ready = false;
  void first.then(() => {
    ready = true;
  });
  window.Cesium = cesium;
  script.onload!();
  await Promise.resolve();
  expect(ready).toBe(false);
  stylesheet.onload!();
  expect(await first).toBe(cesium);
  expect(loadCesium()).toBe(first);
  expect(resources).toHaveLength(2);
});

it("reuses an engine already in memory while ensuring its stylesheet is ready", async () => {
  window.Cesium = cesium;
  const { loadCesium } = await import("./cesiumLoader");
  const pending = loadCesium();
  expect(resources).toHaveLength(1);
  expect(resources[0].tag).toBe("link");
  resources[0].onload!();
  expect(await pending).toBe(cesium);
});

it("retries a failed script without downloading the successful stylesheet again", async () => {
  const { loadCesium } = await import("./cesiumLoader");
  const first = loadCesium();
  const failure = expect(first).rejects.toThrow("engine could not load");
  resources[1].onload!();
  resources[0].onerror!();
  await failure;
  expect(resources[0].remove).toHaveBeenCalledOnce();
  expect(resources[0].onload).toBeNull();

  const retry = loadCesium();
  expect(retry).not.toBe(first);
  expect(resources).toHaveLength(3);
  expect(resources[2].tag).toBe("script");
  window.Cesium = cesium;
  resources[2].onload!();
  expect(await retry).toBe(cesium);
});

it("retries failed styles while sharing the engine that is still downloading", async () => {
  const { loadCesium } = await import("./cesiumLoader");
  const failure = expect(loadCesium()).rejects.toThrow("styles could not load");
  resources[1].onerror!();
  await failure;
  expect(resources[1].remove).toHaveBeenCalledOnce();

  const retry = loadCesium();
  expect(resources).toHaveLength(3);
  expect(resources[2].tag).toBe("link");
  resources[2].onload!();
  window.Cesium = cesium;
  resources[0].onload!();
  expect(await retry).toBe(cesium);
});

it("rejects a downloaded script without an initialized engine and allows recovery", async () => {
  const { loadCesium } = await import("./cesiumLoader");
  const failure = expect(loadCesium()).rejects.toThrow(
    "engine did not initialize",
  );
  resources[1].onload!();
  resources[0].onload!();
  await failure;
  expect(resources[0].remove).toHaveBeenCalledOnce();

  const retry = loadCesium();
  window.Cesium = cesium;
  resources[2].onload!();
  expect(await retry).toBe(cesium);
});
