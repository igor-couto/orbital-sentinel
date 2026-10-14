import { useEffect, useRef, useState } from "react";
import type * as Cesium from "cesium";
import { interpolatePosition } from "../domain/orbits";
import type { OrbitSnapshot, PathView, Trajectory } from "../domain/orbits";
import { Icon } from "./Icon";
import type { ConjunctionVisualization } from "../domain/conjunctions";
import { reconcileGlobeEntities } from "./globeEntities";
import { createLivePolyline } from "./livePolyline";
import { predictionPath } from "../domain/predictionPath";
import type { CatalogMotion } from "../domain/motion";
import { CatalogMotionLayer } from "./catalogMotionLayer";

declare global {
  interface Window {
    Cesium?: typeof Cesium;
    CESIUM_BASE_URL: string;
  }
}

interface Props {
  objects: OrbitSnapshot[];
  motion: CatalogMotion | null;
  selected: OrbitSnapshot | undefined;
  trajectory: Trajectory | null;
  timestamp: number;
  preview: boolean;
  showTrajectory: boolean;
  pathView: PathView;
  onSelect: (id: string) => void;
  encounter?: ConjunctionVisualization | null;
  focusRequest?: number;
}

export function Globe({
  objects,
  motion,
  selected,
  trajectory,
  timestamp,
  preview,
  showTrajectory,
  pathView,
  onSelect,
  encounter,
  focusRequest,
}: Props) {
  const container = useRef<HTMLDivElement>(null);
  const viewerRef = useRef<Cesium.Viewer | null>(null);
  const catalogLayer = useRef<CatalogMotionLayer | null>(null);
  const frameState = useRef({
    selected,
    trajectory,
    timestamp,
    preview,
    showTrajectory,
    pathView,
    encounter,
  });
  frameState.current = {
    selected,
    trajectory,
    timestamp,
    preview,
    showTrajectory,
    pathView,
    encounter,
  };
  const predictionLine = useRef<ReturnType<typeof createLivePolyline> | null>(
    null,
  );
  const [ready, setReady] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const onSelectRef = useRef(onSelect);
  onSelectRef.current = onSelect;
  const selectedId = selected?.object.id;

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
    let animationFrame = 0;
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
        useDefaultRenderLoop: false,
        requestRenderMode: true,
        maximumRenderTimeChange: Infinity,
        skyBox: false,
        skyAtmosphere: new C.SkyAtmosphere(),
        contextOptions: { webgl: { alpha: true } },
      });
      const activeViewer = viewer;
      viewerRef.current = viewer;
      predictionLine.current = createLivePolyline(C);
      catalogLayer.current = new CatalogMotionLayer(C, viewer.scene);
      viewer.scene.backgroundColor = C.Color.fromCssColorString("#080d16");
      viewer.scene.globe.baseColor = C.Color.fromCssColorString("#152b3c");
      viewer.scene.globe.enableLighting = false;
      viewer.scene.globe.showGroundAtmosphere = true;
      viewer.scene.screenSpaceCameraController.minimumZoomDistance = 4_000_000;
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
          } else if (
            picked &&
            typeof picked === "object" &&
            "orbitalObjectId" in picked &&
            typeof picked.orbitalObjectId === "string"
          ) {
            onSelectRef.current(picked.orbitalObjectId);
          }
        },
        C.ScreenSpaceEventType.LEFT_CLICK,
      );
      let lastFrame = 0;
      let measuredFrames = 0,
        measuredTime = 0,
        previousFrame = 0;
      const animate = (wallTime: number) => {
        if (disposed || activeViewer.isDestroyed()) return;
        animationFrame = requestAnimationFrame(animate);
        if (document.hidden || wallTime - lastFrame < 1000 / 30) return;
        lastFrame = wallTime - ((wallTime - lastFrame) % (1000 / 30));
        const state = frameState.current;
        const live = !state.preview && !state.encounter;
        // Preserve the full catalog on slower GPUs. Reduce only canvas resolution,
        // not sample density, marker count, or the accuracy of the displayed clock.
        const elapsed = wallTime - previousFrame;
        previousFrame = wallTime;
        if (live && elapsed > 0 && elapsed < 250) {
          measuredFrames++;
          measuredTime += elapsed;
          if (measuredTime >= 2000) {
            if (
              measuredTime / measuredFrames > 42 &&
              activeViewer.resolutionScale > 0.65
            )
              activeViewer.resolutionScale = Math.max(
                0.65,
                activeViewer.resolutionScale * 0.85,
              );
            measuredFrames = 0;
            measuredTime = 0;
          }
        }
        const time = live ? Date.now() : state.timestamp;
        activeViewer.clock.currentTime = C.JulianDate.fromDate(new Date(time));
        catalogLayer.current?.update(time, !live);
        const path = predictionLine.current;
        if (path && !state.encounter) {
          const vertices =
            state.selected && state.trajectory
              ? predictionPath(
                  state.trajectory,
                  time,
                  1440 / state.selected.elements.meanMotionRevolutionsPerDay,
                  state.pathView,
                ).map((p) => new C.Cartesian3(p.x, p.y, p.z))
              : [];
          // The line and selected marker use exactly the same displayed position.
          const marker =
            state.selected &&
            activeViewer.entities
              .getById(`object:${state.selected.object.id}`)
              ?.position?.getValue(activeViewer.clock.currentTime);
          if (vertices.length && marker) vertices[0] = marker;
          path.setPositions(vertices);
          path.entity.show = state.showTrajectory && vertices.length > 1;
        }
        if (live) activeViewer.scene.requestRender();
        activeViewer.resize();
        activeViewer.render();
      };
      animationFrame = requestAnimationFrame(animate);
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
      cancelAnimationFrame(animationFrame);
      setReady(false);
      viewerRef.current = null;
      predictionLine.current = null;
      catalogLayer.current = null;
      if (viewer && !viewer.isDestroyed()) viewer.destroy();
    };
  }, []);

  useEffect(() => {
    if (!ready) return;
    catalogLayer.current?.setData(motion, objects);
    catalogLayer.current?.hideSelected(selectedId);
    viewerRef.current?.scene.requestRender();
  }, [ready, motion, objects, selectedId]);

  useEffect(() => {
    const viewer = viewerRef.current;
    const C = window.Cesium;
    if (!ready || !viewer || !C) return;
    const entities: Cesium.Entity[] = [];
    const add = (options: Cesium.Entity.ConstructorOptions) =>
      entities.push(new C.Entity(options));
    if (encounter) {
      const tca = Date.parse(encounter.timeOfClosestApproachUtc);
      const pair = [
        {
          orbit: encounter.primary,
          path: encounter.primaryTrajectory,
          color: "#76e3b6",
          label: "A",
        },
        {
          orbit: encounter.secondary,
          path: encounter.secondaryTrajectory,
          color: "#a9caff",
          label: "B",
        },
      ];
      for (const member of pair) {
        const sampled = new C.CallbackPositionProperty(
          (date, result) => {
            const point =
              date &&
              interpolatePosition(
                member.path,
                C.JulianDate.toDate(date).getTime(),
              );
            return point
              ? C.Cartesian3.fromElements(point.x, point.y, point.z, result)
              : undefined;
          },
          false,
          C.ReferenceFrame.FIXED,
        );
        add({
          id: `encounter-object:${member.orbit.object.id}`,
          name: member.orbit.object.name,
          position: sampled,
          availability: new C.TimeIntervalCollection([
            C.TimeInterval.fromIso8601({
              iso8601: `${member.path.startUtc}/${member.path.endUtc}`,
            }),
          ]),
          point: {
            pixelSize: 9,
            color: C.Color.fromCssColorString(member.color),
            outlineColor: C.Color.fromCssColorString("#0b1018"),
            outlineWidth: 2,
          },
          label: {
            text: `${member.label} · ${member.orbit.object.name}`,
            font: "12px sans-serif",
            fillColor: C.Color.fromCssColorString(member.color),
            pixelOffset: new C.Cartesian2(0, member.label === "A" ? -27 : 27),
            showBackground: true,
            backgroundColor:
              C.Color.fromCssColorString("#0d1b27").withAlpha(0.9),
            backgroundPadding: new C.Cartesian2(8, 5),
          },
        });
        // A focused arc around TCA keeps the crossing readable without drawing 24h of loops.
        add({
          id: `encounter-path:${member.label}`,
          polyline: {
            positions: member.path.points
              .filter(
                (point) =>
                  Math.abs(Date.parse(point.timestampUtc) - tca) <= 30 * 60_000,
              )
              .map(
                (point) =>
                  new C.Cartesian3(
                    point.positionEcefMeters.x,
                    point.positionEcefMeters.y,
                    point.positionEcefMeters.z,
                  ),
              ),
            width: 2,
            arcType: C.ArcType.NONE,
            material: C.Color.fromCssColorString(member.color).withAlpha(0.85),
          },
        });
      }
      const a = encounter.primaryPositionEcefMeters;
      const b = encounter.secondaryPositionEcefMeters;
      add({
        id: "encounter-tca",
        position: new C.Cartesian3(
          (a.x + b.x) / 2,
          (a.y + b.y) / 2,
          (a.z + b.z) / 2,
        ),
        point: {
          pixelSize: 22,
          color: C.Color.TRANSPARENT,
          outlineColor: C.Color.WHITE,
          outlineWidth: 2,
        },
        label: {
          text: `${encounter.source === "Simulation" ? "SIMULATION · " : ""}CLOSEST APPROACH`,
          font: "11px sans-serif",
          fillColor: C.Color.WHITE,
          pixelOffset: new C.Cartesian2(0, -54),
          showBackground: true,
          backgroundColor: C.Color.fromCssColorString("#0d1b27").withAlpha(0.9),
          backgroundPadding: new C.Cartesian2(8, 5),
        },
      });
      add({
        id: "encounter-separation",
        polyline: {
          positions: [
            new C.Cartesian3(a.x, a.y, a.z),
            new C.Cartesian3(b.x, b.y, b.z),
          ],
          width: 2,
          arcType: C.ArcType.NONE,
          material: C.Color.WHITE,
        },
      });
      reconcileGlobeEntities(viewer.entities, entities);
      viewer.scene.requestRender();
      return;
    }
    if (selected) {
      const sampled = new C.CallbackPositionProperty(
        (date, result) => {
          if (!date) return undefined;
          result ??= new C.Cartesian3();
          const time = C.JulianDate.toDate(date).getTime();
          if (!frameState.current.preview) {
            const current = catalogLayer.current?.position(
              selected.object.id,
              selected.version,
              time,
              result,
            );
            if (current) return current;
          }
          const point = trajectory && interpolatePosition(trajectory, time);
          return point
            ? C.Cartesian3.fromElements(point.x, point.y, point.z, result)
            : undefined;
        },
        false,
        C.ReferenceFrame.FIXED,
      );
      add({
        id: `object:${selected.object.id}`,
        name: selected.object.name,
        position: sampled,
        point: {
          pixelSize: 8,
          color: C.Color.fromCssColorString("#f3faf6"),
          outlineColor: C.Color.fromCssColorString("#76e3b6"),
          outlineWidth: 2,
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
      if (predictionLine.current) entities.push(predictionLine.current.entity);
    }
    reconcileGlobeEntities(viewer.entities, entities);
    viewer.scene.requestRender();
  }, [ready, selected, trajectory, encounter]);

  useEffect(() => {
    const viewer = viewerRef.current;
    const C = window.Cesium;
    if (!ready || !viewer || !C) return;
    viewer.scene.requestRender();
  }, [ready, timestamp, preview, showTrajectory, pathView, encounter]);

  useEffect(() => {
    const viewer = viewerRef.current;
    const C = window.Cesium;
    if (!ready || !viewer || !C || !encounter) return;
    const a = encounter.primaryPositionEcefMeters;
    const b = encounter.secondaryPositionEcefMeters;
    const location = C.Cartographic.fromCartesian(
      new C.Cartesian3((a.x + b.x) / 2, (a.y + b.y) / 2, (a.z + b.z) / 2),
    );
    viewer.camera.flyTo({
      destination: C.Cartesian3.fromRadians(
        location.longitude,
        location.latitude,
        Math.max(8_000_000, location.height * 2),
      ),
      duration: 1,
    });
  }, [ready, encounter, focusRequest]);

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
    if (!viewer || !C) return;
    if (encounter) {
      const a = encounter.primaryPositionEcefMeters;
      const b = encounter.secondaryPositionEcefMeters;
      const location = C.Cartographic.fromCartesian(
        new C.Cartesian3((a.x + b.x) / 2, (a.y + b.y) / 2, (a.z + b.z) / 2),
      );
      viewer.camera.flyTo({
        destination: C.Cartesian3.fromRadians(
          location.longitude,
          location.latitude,
          Math.max(8_000_000, location.height * 2),
        ),
        duration: 1,
      });
      return;
    }
    if (!selectedId) return;
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
          title={encounter ? "Focus encounter" : "Focus selected object"}
          aria-label={encounter ? "Focus encounter" : "Focus selected object"}
          disabled={(!selected && !encounter) || !ready}
          onClick={focusSelected}
        >
          <Icon name="crosshair" />
        </button>
      </div>
    </>
  );
}
