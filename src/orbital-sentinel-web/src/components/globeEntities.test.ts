import { describe, expect, it } from "vitest";
import {
  Cartesian3,
  ConstantProperty,
  Entity,
  EntityCollection,
  JulianDate,
  SampledPositionProperty,
  TimeInterval,
  TimeIntervalCollection,
} from "cesium";
import { reconcileGlobeEntities } from "./globeEntities";

const start = JulianDate.fromIso8601("2026-10-13T12:00:00Z");
const end = JulianDate.addSeconds(start, 60, new JulianDate());
const middle = JulianDate.addSeconds(start, 30, new JulianDate());
const a = new Cartesian3(7_000_000, 0, 0);
const b = new Cartesian3(0, 7_000_000, 0);

function marker(id: string, position: Cartesian3) {
  return new Entity({ id, position, point: { pixelSize: 5 } });
}

function selected(id: string, from: Cartesian3, to: Cartesian3) {
  const position = new SampledPositionProperty();
  position.addSample(start, from);
  position.addSample(end, to);
  return new Entity({
    id,
    position,
    availability: new TimeIntervalCollection([
      new TimeInterval({ start, stop: end }),
    ]),
    point: { pixelSize: 10 },
    label: { text: id },
  });
}

describe("globe entity updates", () => {
  it("keeps Cesium's retained references in sync across selection and position refreshes", () => {
    const collection = new EntityCollection();
    // Visualizers retain references delivered through collectionChanged.
    const rendered = new Map<string, Entity>();
    collection.collectionChanged.addEventListener((_, added, removed) => {
      for (const entity of removed) rendered.delete(entity.id);
      for (const entity of added) rendered.set(entity.id, entity);
    });
    reconcileGlobeEntities(collection, [selected("a", a, b), marker("b", b)]);
    const originalA = rendered.get("a")!;
    const originalB = rendered.get("b")!;

    const refreshedA = new Cartesian3(6_800_000, 100_000, 0);
    reconcileGlobeEntities(collection, [
      marker("a", refreshedA),
      selected("b", b, a),
    ]);

    expect(collection.getById("a")).toBe(originalA);
    expect(collection.getById("b")).toBe(originalB);
    expect(originalA.position?.getValue(middle)).toEqual(refreshedA);
    expect(originalA.point?.pixelSize?.getValue(middle)).toBe(5);
    expect(originalA.label).toBeUndefined();
    expect(originalA.availability).toBeUndefined();
    expect(originalB.point?.pixelSize?.getValue(middle)).toBe(10);
    expect(originalB.label?.text?.getValue(middle)).toBe("b");
    expect(originalB.position?.getValue(middle)).toEqual(
      Cartesian3.midpoint(a, b, new Cartesian3()),
    );

    // Returning from preview must restore a previously hidden catalog marker.
    originalA.show = false;
    reconcileGlobeEntities(collection, [marker("a", a), selected("b", b, a)]);
    expect(originalA.show).toBe(true);
    expect(originalA.position?.getValue(middle)).toEqual(a);
  });

  it("updates the path retained by Cesium along with the selected marker", () => {
    const collection = new EntityCollection();
    const path = (positions: Cartesian3[]) =>
      new Entity({ id: "prediction-path", polyline: { positions } });
    reconcileGlobeEntities(collection, [selected("a", a, b), path([a, b])]);
    const renderedPath = collection.getById("prediction-path")!;

    reconcileGlobeEntities(collection, [selected("b", b, a), path([])]);
    const position = collection.getById("b")!.position!.getValue(middle)!;
    collection.getById("prediction-path")!.polyline!.positions =
      new ConstantProperty([position, a]);

    expect(collection.getById("prediction-path")).toBe(renderedPath);
    expect(renderedPath.polyline?.positions?.getValue(middle)[0]).toEqual(
      position,
    );
    expect(collection.getById("a")).toBeUndefined();
  });

  it("notifies visualizers when switching between catalog and encounter entities", () => {
    const collection = new EntityCollection();
    const rendered = new Map<string, Entity>();
    collection.collectionChanged.addEventListener((_, added, removed) => {
      for (const entity of removed) rendered.delete(entity.id);
      for (const entity of added) rendered.set(entity.id, entity);
    });
    reconcileGlobeEntities(collection, [marker("object:a", a)]);
    reconcileGlobeEntities(collection, [
      selected("encounter-object:a", a, b),
      selected("encounter-object:b", b, a),
    ]);
    expect([...rendered.keys()]).toEqual([
      "encounter-object:a",
      "encounter-object:b",
    ]);

    reconcileGlobeEntities(collection, [marker("object:a", a)]);
    expect([...rendered.keys()]).toEqual(["object:a"]);
    expect(rendered.get("object:a")).toBe(collection.getById("object:a"));
    reconcileGlobeEntities(collection, []);
    expect(rendered.size).toBe(0);
  });
});
