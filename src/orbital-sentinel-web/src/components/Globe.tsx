import { useEffect, useRef, useState } from "react";
import type * as Cesium from "cesium";
import {
  interpolatePosition,
  isCurrentPosition,
  kindColor,
  objectKind,
} from "../domain/orbits";
import type {
  ObjectPosition,
  OrbitSnapshot,
  Trajectory,
} from "../domain/orbits";
import { Icon } from "./Icon";

declare global {
  interface Window {
    Cesium?: typeof Cesium;
    CESIUM_BASE_URL: string;
  }
}

interface Props {
  objects: OrbitSnapshot[];
  positions: ObjectPosition[];
  selected: OrbitSnapshot | undefined;
  trajectory: Trajectory | null;
  timestamp: number;
  preview: boolean;
  showTrajectory: boolean;
  onSelect: (id: string) => void;
}

export function Globe({
  objects,
  positions,
  selected,
  trajectory,
  timestamp,
  preview,
  showTrajectory,
  onSelect,
}: Props) {
  const container = useRef<HTMLDivElement>(null);
  const viewerRef = useRef<Cesium.Viewer | null>(null);
  const [ready, setReady] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const onSelectRef = useRef(onSelect);
  onSelectRef.current = onSelect;
  const selectedId = selected?.object.id;
  const periodMinutes = selected
    ? 1440 / selected.elements.meanMotionRevolutionsPerDay
    : 90;

  useEffect(() => {
    const C = window.Cesium;
    if (!C || !container.current) {
      setError(
        "The globe engine could not load. Refresh the page to try again.",
      );
      return;
    }
    window.CESIUM_BASE_URL = "/cesium/";
    let disposed = false;
    let viewer: Cesium.Viewer | null = null;
    try {
      // Explicitly disable ion-backed default imagery and geocoding. All globe
      // resources and Natural Earth imagery are served from this application.
      viewer = new C.Viewer(container.current, {
        baseLayer: false,
        baseLayerPicker: false,
        geocoder: false,
        animation: false,
        timeline: false,
        homeButton: false,
        fullscreenButton: false,
        navigationHelpButton: false,
        sceneModePicker: false,
        infoBox: false,
        selectionIndicator: false,
        shouldAnimate: false,
        requestRenderMode: true,
        maximumRenderTimeChange: Infinity,
        skyBox: false,
        skyAtmosphere: new C.SkyAtmosphere(),
        contextOptions: { webgl: { alpha: true } },
      });
      const activeViewer = viewer;
      viewerRef.current = viewer;
      viewer.scene.backgroundColor = C.Color.fromCssColorString("#080d16");
      viewer.scene.globe.baseColor = C.Color.fromCssColorString("#152b3c");
      viewer.scene.globe.enableLighting = false;
      viewer.scene.globe.showGroundAtmosphere = true;
      viewer.scene.screenSpaceCameraController.minimumZoomDistance = 7_000_000;
      viewer.scene.screenSpaceCameraController.maximumZoomDistance = 150_000_000;
      viewer.resolutionScale = Math.min(window.devicePixelRatio, 1.5);
      viewer.camera.setView({
        destination: C.Cartesian3.fromDegrees(18, 20, 16_000_000),
      });
      viewer.cesiumWidget.creditDisplay.addStaticCredit(
        new C.Credit("Natural Earth", true),
      );
      viewer.screenSpaceEventHandler.setInputAction(
        (event: Cesium.ScreenSpaceEventHandler.PositionedEvent) => {
          const picked: unknown = activeViewer.scene.pick(event.position);
          if (
            picked &&
            typeof picked === "object" &&
            "id" in picked &&
            picked.id instanceof C.Entity
          ) {
            const id = picked.id.id;
            if (id.startsWith("object:")) onSelectRef.current(id.slice(7));
          }
        },
        C.ScreenSpaceEventType.LEFT_CLICK,
      );
      void C.TileMapServiceImageryProvider.fromUrl(
        "/cesium/Assets/Textures/NaturalEarthII",
        {
          maximumLevel: 2,
        },
      )
        .then((provider) => {
          if (disposed || activeViewer.isDestroyed()) return;
          const layer = activeViewer.imageryLayers.addImageryProvider(provider);
          layer.brightness = 0.68;
          layer.saturation = 0.65;
          layer.contrast = 1.12;
          activeViewer.scene.requestRender();
        })
        .catch(() => {
          if (!disposed)
            setError(
              "Earth imagery is unavailable. Orbital positions remain visible on the globe.",
            );
        });
      setReady(true);
    } catch {
      setError(
        "The 3D globe needs a browser with WebGL support. The object catalog is still available.",
      );
    }
    return () => {
      disposed = true;
      setReady(false);
      viewerRef.current = null;
      if (viewer && !viewer.isDestroyed()) viewer.destroy();
    };
  }, []);

  useEffect(() => {
    const viewer = viewerRef.current;
    const C = window.Cesium;
    if (!ready || !viewer || !C) return;
    const lookup = new Map(objects.map((item) => [item.object.id, item]));
    viewer.entities.suspendEvents();
    viewer.entities.removeAll();
    for (const position of positions) {
      const orbit = lookup.get(position.orbitalObjectId);
      if (!orbit || (position.orbitalObjectId === selectedId && trajectory))
        continue;
      const { x, y, z } = position.positionEcefMeters;
      const color = C.Color.fromCssColorString(
        kindColor[objectKind(orbit.object.objectType)],
      );
      viewer.entities.add({
        id: `object:${position.orbitalObjectId}`,
        name: orbit.object.name,
        position: new C.Cartesian3(x, y, z),
        point: {
          pixelSize: position.orbitalObjectId === selectedId ? 9 : 5,
          color,
          outlineColor: C.Color.fromCssColorString("#07151b"),
          outlineWidth: 1.5,
        },
      });
    }
    if (trajectory && selected) {
      // API positions are Earth-fixed metres, so use FIXED (not TEME / INERTIAL).
      const sampled = new C.SampledPositionProperty(C.ReferenceFrame.FIXED);
      sampled.setInterpolationOptions({
        interpolationDegree: 1,
        interpolationAlgorithm: C.LinearApproximation,
      });
      for (const point of trajectory.points) {
        const { x, y, z } = point.positionEcefMeters;
        sampled.addSample(
          C.JulianDate.fromIso8601(point.timestampUtc),
          new C.Cartesian3(x, y, z),
        );
      }
      viewer.entities.add({
        id: `object:${selected.object.id}`,
        name: selected.object.name,
        position: sampled,
        availability: new C.TimeIntervalCollection([
          C.TimeInterval.fromIso8601({
            iso8601: `${trajectory.startUtc}/${trajectory.endUtc}`,
          }),
        ]),
        point: {
          pixelSize: 10,
          color: C.Color.fromCssColorString("#f3faf6"),
          outlineColor: C.Color.fromCssColorString("#76e3b6"),
          outlineWidth: 3,
        },
        label: {
          text: selected.object.name,
          font: "12px sans-serif",
          fillColor: C.Color.fromCssColorString("#e5f6ed"),
          pixelOffset: new C.Cartesian2(0, -23),
          showBackground: true,
          backgroundColor: C.Color.fromCssColorString("#0d1b27").withAlpha(0.9),
          backgroundPadding: new C.Cartesian2(8, 5),
          scale: 1,
        },
      });
      viewer.entities.add({
        id: "prediction-path",
        polyline: {
          positions: [],
          width: 1.6,
          material: C.Color.fromCssColorString("#76e3b6").withAlpha(0.75),
          arcType: C.ArcType.NONE,
        },
      });
    }
    viewer.entities.resumeEvents();
    viewer.scene.requestRender();
  }, [ready, objects, positions, selected, selectedId, trajectory]);

  useEffect(() => {
    const viewer = viewerRef.current;
    const C = window.Cesium;
    if (!ready || !viewer || !C) return;
    viewer.clock.currentTime = C.JulianDate.fromDate(new Date(timestamp));
    for (const position of positions) {
      if (position.orbitalObjectId === selectedId && trajectory) continue;
      const entity = viewer.entities.getById(
        `object:${position.orbitalObjectId}`,
      );
      if (entity)
        entity.show = !preview && isCurrentPosition(position, timestamp);
    }
    const path = viewer.entities.getById("prediction-path");
    if (path?.polyline && trajectory) {
      const start = Math.max(timestamp, Date.parse(trajectory.startUtc));
      const end = start + Math.min(periodMinutes, 180) * 60_000;
      const points = trajectory.points.filter(
        (point) =>
          Date.parse(point.timestampUtc) >= start &&
          Date.parse(point.timestampUtc) <= end,
      );
      const pathPositions = points.map(
        ({ positionEcefMeters: { x, y, z } }) => new C.Cartesian3(x, y, z),
      );
      const firstPosition = interpolatePosition(trajectory, start);
      if (
        firstPosition &&
        (!points.length || Date.parse(points[0].timestampUtc) !== start)
      ) {
        pathPositions.unshift(
          new C.Cartesian3(firstPosition.x, firstPosition.y, firstPosition.z),
        );
      }
      path.show = showTrajectory && pathPositions.length > 1;
      path.polyline.positions = new C.ConstantProperty(pathPositions);
    }
    viewer.scene.requestRender();
  }, [
    ready,
    timestamp,
    preview,
    positions,
    trajectory,
    selectedId,
    showTrajectory,
    periodMinutes,
    objects,
    selected,
  ]);

  function resetCamera() {
    const C = window.Cesium;
    if (C)
      viewerRef.current?.camera.flyTo({
        destination: C.Cartesian3.fromDegrees(18, 20, 16_000_000),
        duration: 1,
      });
  }

  function focusSelected() {
    const viewer = viewerRef.current;
    const C = window.Cesium;
    if (!viewer || !selectedId || !C) return;
    const position = viewer.entities
      .getById(`object:${selectedId}`)
      ?.position?.getValue(viewer.clock.currentTime);
    if (!position) return;
    const cartographic = C.Cartographic.fromCartesian(position);
    viewer.camera.flyTo({
      destination: C.Cartesian3.fromRadians(
        cartographic.longitude,
        cartographic.latitude,
        Math.max(8_000_000, cartographic.height * 2),
      ),
      duration: 1,
    });
  }

  return (
    <>
      <div
        ref={container}
        className="globe-canvas"
        aria-label="Interactive Earth showing backend-predicted orbital positions"
      />
      {error && (
        <div role="status" className="globe-warning">
          <Icon name="info" />
          {error}
        </div>
      )}
      <div className="globe-controls">
        <button
          className="icon-button"
          title="Reset Earth view"
          aria-label="Reset Earth view"
          onClick={resetCamera}
        >
          <Icon name="globe" />
        </button>
        <button
          className="icon-button"
          title="Focus selected object"
          aria-label="Focus selected object"
          disabled={!selected || !ready}
          onClick={focusSelected}
        >
          <Icon name="crosshair" />
        </button>
      </div>
    </>
  );
}
