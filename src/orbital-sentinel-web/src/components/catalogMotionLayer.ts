import type * as Cesium from "cesium";
import { kindColor, objectKind } from "../domain/orbits";
import type { ObjectKind, OrbitSnapshot } from "../domain/orbits";
import { motionInterval, sampleMotion } from "../domain/motion";
import type { CatalogMotion, ObjectMotion } from "../domain/motion";

/** One packed position upload per frame, with stable pick identities across refreshes. */
export class CatalogMotionLayer {
  private collection: Cesium.BufferPointCollection | null = null;
  private motion: CatalogMotion | null = null;
  private rows: ObjectMotion[] = [];
  private indices = new Map<string, number>();
  private positions = new Float64Array();
  private point: Cesium.BufferPoint;
  private hiddenId: string | undefined;
  private objects: OrbitSnapshot[] | null = null;
  private sphere: Cesium.BoundingSphere;
  private kinds: ObjectKind[] = [];
  private materials = new Map<ObjectKind, Cesium.BufferPointMaterial>();

  constructor(
    private C: typeof Cesium,
    private scene: Cesium.Scene,
  ) {
    this.point = new C.BufferPoint();
    this.sphere = new C.BoundingSphere(C.Cartesian3.ZERO, 1);
    for (const [kind, color] of Object.entries(kindColor))
      this.materials.set(
        kind as ObjectKind,
        new C.BufferPointMaterial({
          size: 4,
          outlineWidth: 1,
          color: C.Color.fromCssColorString(color),
          outlineColor: C.Color.fromCssColorString("#07151b"),
        }),
      );
  }

  setData(motion: CatalogMotion | null, objects: OrbitSnapshot[]) {
    if (motion === this.motion && objects === this.objects) return;
    const metadataChanged = objects !== this.objects;
    this.motion = motion;
    this.objects = objects;
    const lookup = new Map(objects.map((o) => [o.object.id, o]));
    const rows =
      motion?.objects.filter((o) => lookup.has(o.orbitalObjectId)) ?? [];
    const changed =
      rows.length !== this.rows.length ||
      rows.some(
        (row, i) => row.orbitalObjectId !== this.rows[i].orbitalObjectId,
      );
    this.rows = rows;
    if (changed || !this.collection) {
      this.kinds = [];
      if (this.collection) this.scene.primitives.remove(this.collection);
      this.collection = this.scene.primitives.add(
        new this.C.BufferPointCollection({
          primitiveCountMax: Math.max(rows.length, 1),
          allowPicking: true,
          blendOption: this.C.BlendOption.OPAQUE,
          boundingVolume: this.sphere,
        }),
      );
      this.positions = new Float64Array(rows.length * 3);
      this.indices.clear();
      rows.forEach((row, index) => {
        this.indices.set(row.orbitalObjectId, index);
        this.collection!.add(
          {
            position: this.C.Cartesian3.ZERO,
            pickObject: { orbitalObjectId: row.orbitalObjectId },
          },
          this.point,
        );
      });
    }
    let radiusSquared = 1;
    rows.forEach((row, index) => {
      if (changed || metadataChanged) {
        const kind = objectKind(
          lookup.get(row.orbitalObjectId)!.object.objectType,
        );
        if (this.kinds[index] !== kind) {
          this.collection!.get(index, this.point);
          this.point.show = row.orbitalObjectId !== this.hiddenId;
          this.point.setMaterial(this.materials.get(kind)!);
          this.kinds[index] = kind;
        }
      }
      for (let i = 0; i < row.states.length; i += 6)
        radiusSquared = Math.max(
          radiusSquared,
          row.states[i] ** 2 + row.states[i + 1] ** 2 + row.states[i + 2] ** 2,
        );
    });
    // All samples plus a generous interpolation margin bound this rotating-Earth scene.
    this.sphere.radius = Math.sqrt(radiusSquared) + 100_000;
    this.sphere.clone(this.collection!.boundingVolume);
  }

  hideSelected(id: string | undefined) {
    if (id === this.hiddenId) return;
    for (const [key, show] of [
      [this.hiddenId, true],
      [id, false],
    ] as const) {
      const index = key ? this.indices.get(key) : undefined;
      if (index !== undefined) {
        this.collection!.get(index, this.point);
        this.point.show = show;
      }
    }
    this.hiddenId = id;
  }

  position(
    id: string,
    version: number,
    time: number,
    result: Cesium.Cartesian3,
  ): Cesium.Cartesian3 | undefined {
    const index = this.indices.get(id);
    const row = index === undefined ? undefined : this.rows[index];
    if (!this.motion || !row || row.orbitVersion !== version) return undefined;
    return sampleMotion(this.motion, row, time, result) ? result : undefined;
  }

  update(time: number, hidden: boolean) {
    if (!this.collection) return;
    const interval = this.motion && motionInterval(this.motion, time);
    this.collection.show = !hidden && !!interval && this.rows.length > 0;
    if (!this.collection.show || !interval) return;
    const { offset: i, weights: w } = interval;
    for (let row = 0; row < this.rows.length; row++) {
      const s = this.rows[row].states;
      for (let axis = 0; axis < 3; axis++)
        this.positions[row * 3 + axis] =
          w[0] * s[i + axis] +
          w[1] * s[i + axis + 3] +
          w[2] * s[i + axis + 6] +
          w[3] * s[i + axis + 9];
    }
    this.collection.setPositions(this.positions, 0, this.rows.length);
  }
}
