import { useEffect, useRef, useState, type ReactNode } from "react";
import type { OrbitSnapshot } from "../domain/orbits";
import { kindLabel, objectKind } from "../domain/orbits";
import { Icon } from "./Icon";

const rowHeight = 72;

/** Only visible rows are mounted; filtering and selection use the whole catalog. */
export function CatalogList({
  items,
  selectedId,
  onSelect,
  loading,
  children,
}: {
  items: OrbitSnapshot[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  loading: boolean;
  children: ReactNode;
}) {
  const container = useRef<HTMLDivElement>(null);
  const [top, setTop] = useState(0);
  const [height, setHeight] = useState(600);
  const start = Math.max(
    0,
    Math.min(Math.floor(top / rowHeight) - 5, items.length - 1),
  );
  const end = Math.min(
    items.length,
    start + Math.ceil(height / rowHeight) + 10,
  );
  const latestItems = useRef(items);
  latestItems.current = items;
  useEffect(() => {
    const node = container.current!;
    const resize = new ResizeObserver(() => setHeight(node.clientHeight));
    resize.observe(node);
    return () => resize.disconnect();
  }, []);
  useEffect(() => {
    const index = latestItems.current.findIndex(
      (item) => item.object.id === selectedId,
    );
    const node = container.current;
    if (!node || index < 0) return;
    const rowTop = index * rowHeight;
    if (
      rowTop < node.scrollTop ||
      rowTop + rowHeight > node.scrollTop + node.clientHeight
    )
      node.scrollTop = rowTop;
  }, [selectedId]);
  const visible = items.slice(start, end);
  return (
    <div
      className="catalog-list"
      ref={container}
      tabIndex={0}
      role="listbox"
      aria-label="Orbital objects"
      aria-busy={loading}
      aria-activedescendant={
        visible.some((item) => item.object.id === selectedId)
          ? `catalog-${selectedId}`
          : undefined
      }
      onScroll={(event) => setTop(event.currentTarget.scrollTop)}
      onKeyDown={(event) => {
        const index = items.findIndex((item) => item.object.id === selectedId);
        const next =
          event.key === "ArrowDown"
            ? index + 1
            : event.key === "ArrowUp"
              ? index - 1
              : event.key === "Home"
                ? 0
                : event.key === "End"
                  ? items.length - 1
                  : null;
        if (next === null || !items.length) return;
        event.preventDefault();
        onSelect(
          items[Math.max(0, Math.min(next, items.length - 1))].object.id,
        );
      }}
    >
      {children}
      <div aria-hidden="true" style={{ height: start * rowHeight }} />
      {visible.map((item, offset) => {
        const kind = objectKind(item.object.objectType);
        return (
          <button
            key={item.object.id}
            id={`catalog-${item.object.id}`}
            role="option"
            aria-selected={selectedId === item.object.id}
            aria-posinset={start + offset + 1}
            aria-setsize={items.length}
            tabIndex={-1}
            className={`object-row ${selectedId === item.object.id ? "selected" : ""}`}
            style={{ height: rowHeight, minHeight: rowHeight }}
            onClick={() => onSelect(item.object.id)}
          >
            <span className={`object-marker ${kind}`}>
              <Icon name={kind === "payload" ? "orbit" : "layers"} size={17} />
            </span>
            <span className="object-text">
              <strong>{item.object.name}</strong>
              <span>
                {item.object.noradCatalogId === null
                  ? "No NORAD ID"
                  : `NORAD ${item.object.noradCatalogId}`}
                <span className="text-dot">·</span>
                {kindLabel[kind]}
              </span>
            </span>
            <Icon name="chevron" size={13} />
          </button>
        );
      })}
      <div
        aria-hidden="true"
        style={{ height: (items.length - end) * rowHeight }}
      />
    </div>
  );
}
