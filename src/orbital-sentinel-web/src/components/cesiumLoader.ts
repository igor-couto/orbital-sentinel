import type * as Cesium from "cesium";

declare global {
  interface Window {
    Cesium?: typeof Cesium;
    CESIUM_BASE_URL: string;
  }
}

let engine: Promise<typeof Cesium> | undefined;
let stylesheet: Promise<void> | undefined;
let resources: Promise<typeof Cesium> | undefined;

function loadEngine(): Promise<typeof Cesium> {
  window.CESIUM_BASE_URL = "/cesium/";
  if (window.Cesium) return Promise.resolve(window.Cesium);
  if (engine) return engine;
  engine = new Promise<typeof Cesium>((resolve, reject) => {
    const script = document.createElement("script");
    script.src = "/cesium/Cesium.js";
    script.async = true;
    script.onload = () => {
      script.onload = script.onerror = null;
      if (window.Cesium) resolve(window.Cesium);
      else {
        script.remove();
        reject(new Error("The globe engine did not initialize."));
      }
    };
    script.onerror = () => {
      script.onload = script.onerror = null;
      script.remove();
      reject(new Error("The globe engine could not load."));
    };
    document.head.appendChild(script);
  }).catch((error: unknown) => {
    engine = undefined;
    throw error;
  });
  return engine;
}

function loadStylesheet(): Promise<void> {
  if (stylesheet) return stylesheet;
  stylesheet = new Promise<void>((resolve, reject) => {
    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = "/cesium/Widgets/widgets.css";
    link.onload = () => {
      link.onload = link.onerror = null;
      resolve();
    };
    link.onerror = () => {
      link.onload = link.onerror = null;
      link.remove();
      reject(new Error("The globe styles could not load."));
    };
    document.head.appendChild(link);
  }).catch((error: unknown) => {
    stylesheet = undefined;
    throw error;
  });
  return stylesheet;
}

/** Shared across mounts, including StrictMode. A failed resource can be retried. */
export function loadCesium(): Promise<typeof Cesium> {
  resources ??= Promise.all([loadEngine(), loadStylesheet()])
    .then(([cesium]) => cesium)
    .catch((error: unknown) => {
      resources = undefined;
      throw error;
    });
  return resources;
}
