import type { Entity, EntityCollection } from "cesium";

/** Update the graphics we own without replacing an entity under the same ID. */
export function reconcileGlobeEntities(
  collection: EntityCollection,
  desired: Entity[],
): void {
  const ids = new Set(desired.map((entity) => entity.id));
  collection.suspendEvents();
  try {
    for (const next of desired) {
      const current = collection.getById(next.id);
      if (!current) {
        collection.add(next);
        continue;
      }

      // Cesium visualizers retain this instance. Removing and re-adding its ID
      // within one event batch cancels the notification and leaves old graphics.
      current.name = next.name;
      current.availability = next.availability;
      current.position = next.position;
      current.point = next.point;
      current.label = next.label;
      current.polyline = next.polyline;
      current.show = next.show;
    }
    for (const current of [...collection.values]) {
      if (!ids.has(current.id)) collection.remove(current);
    }
  } finally {
    collection.resumeEvents();
  }
}
