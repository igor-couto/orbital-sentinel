import { expect, it } from "vitest";
import * as Cesium from "cesium";
import { createLivePolyline } from "./livePolyline";
import { reconcileGlobeEntities } from "./globeEntities";

it("retains dynamic geometry across clock ticks and catalog refreshes", () => {
  const line = createLivePolyline(Cesium);
  const collection = new Cesium.EntityCollection();
  const property = line.entity.polyline!.positions!;
  let geometryChanges = 0;
  line.entity.definitionChanged.addEventListener((_, name) => {
    if (name === "polyline") geometryChanges++;
  });
  expect(property.isConstant).toBe(false);
  for (let tick = 0; tick < 30; tick++) {
    const points = [
      new Cesium.Cartesian3(tick, 7e6, 0),
      new Cesium.Cartesian3(7e6, tick, 0),
    ];
    line.setPositions(points);
    reconcileGlobeEntities(collection, [line.entity]);
    expect(collection.getById("prediction-path")).toBe(line.entity);
    expect(line.entity.polyline!.positions).toBe(property);
    expect(property.getValue()).toEqual(points);
  }
  expect(geometryChanges).toBe(0);
});
