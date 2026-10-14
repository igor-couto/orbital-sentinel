import type * as Cesium from "cesium";

/** Keep Cesium's dynamic line alive while replacing its vertices synchronously. */
export function createLivePolyline(C: typeof Cesium) {
  let positions: Cesium.Cartesian3[] = [];
  const entity = new C.Entity({
    id: "prediction-path",
    polyline: {
      // A changing ConstantProperty rebuilds static geometry asynchronously and
      // leaves blank frames. This uses Cesium's persistent dynamic polyline.
      positions: new C.CallbackProperty(() => positions, false),
      width: 1.6,
      material: C.Color.fromCssColorString("#76e3b6").withAlpha(0.75),
      arcType: C.ArcType.NONE,
    },
  });
  return {
    entity,
    setPositions(next: Cesium.Cartesian3[]) {
      positions = next;
    },
  };
}
