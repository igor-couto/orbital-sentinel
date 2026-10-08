import { cp, mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";

// Use Cesium's official, prebuilt browser distribution. Vite only builds the app,
// keeping CI memory bounded and keeping Cesium's worker/resource URLs intact.
const source = fileURLToPath(
  new URL("../node_modules/cesium/Build/Cesium/", import.meta.url),
);
const target = fileURLToPath(new URL("../public/cesium/", import.meta.url));
await mkdir(target, { recursive: true });
await cp(source, target, {
  recursive: true,
  filter: (path) => !path.endsWith(".map"),
});
