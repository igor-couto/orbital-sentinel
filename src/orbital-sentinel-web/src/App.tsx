import { useEffect, useMemo, useRef, useState } from "react";
import { Globe } from "./components/Globe";
import { Icon } from "./components/Icon";
import {
  elementAge,
  formatUtc,
  interpolatePosition,
  isCurrentPosition,
  kindLabel,
  objectKind,
} from "./domain/orbits";
import type { ObjectKind, OrbitSnapshot } from "./domain/orbits";
import { useSelectedTrajectory, useTrafficData } from "./hooks/useTrafficData";

const emptyObjects: OrbitSnapshot[] = [];
const filterOptions: Array<{ id: "all" | ObjectKind; label: string }> = [
  { id: "all", label: "All objects" },
  { id: "payload", label: "Payloads" },
  { id: "debris", label: "Debris" },
  { id: "rocket", label: "Rockets" },
];

export function App() {
  const data = useTrafficData();
  const [now, setNow] = useState(Date.now);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [filter, setFilter] = useState<"all" | ObjectKind>("all");
  const [previewTime, setPreviewTime] = useState<number | null>(null);
  const [showTrajectory, setShowTrajectory] = useState(true);
  const about = useRef<HTMLDialogElement>(null);
  const privacy = useRef<HTMLDialogElement>(null);
  const objects = data.catalog.value?.items ?? emptyObjects;
  const selected = objects.find((item) => item.object.id === selectedId);
  const trajectoryState = useSelectedTrajectory(
    selected,
    data.trajectoryUpdates[selectedId ?? ""] ?? 0,
    data.revision,
  );
  const trajectory = trajectoryState.value;
  const timestamp = previewTime ?? now;
  const predictionExpired =
    trajectory !== null && now > Date.parse(trajectory.endUtc);
  const currentPosition = trajectory
    ? interpolatePosition(trajectory, timestamp)
    : undefined;
  const visiblePositions =
    data.positions.value?.filter((position) =>
      isCurrentPosition(position, now),
    ) ?? [];

  useEffect(() => {
    const interval = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(interval);
  }, []);
  useEffect(() => {
    if (
      objects.length &&
      !objects.some((item) => item.object.id === selectedId)
    )
      setSelectedId(objects[0].object.id);
  }, [objects, selectedId]);

  function selectObject(id: string) {
    setSelectedId(id);
    setPreviewTime(null);
  }
  const filtered = useMemo(
    () =>
      objects.filter((item) => {
        const query = search.trim().toLowerCase();
        return (
          (filter === "all" || objectKind(item.object.objectType) === filter) &&
          (!query ||
            item.object.name.toLowerCase().includes(query) ||
            String(item.object.noradCatalogId ?? "").includes(query) ||
            (item.object.internationalDesignator ?? "")
              .toLowerCase()
              .includes(query))
        );
      }),
    [objects, search, filter],
  );
  const age = selected ? elementAge(selected.elements.epochUtc, now) : null;
  const connectionLabel = {
    connecting: "Connecting",
    live: "Updates connected",
    reconnecting: "Reconnecting",
    polling: "Polling fallback",
  }[data.connection];
  const positionError = data.positions.error;
  const trajectoryStart = trajectory ? Date.parse(trajectory.startUtc) : now;
  const trajectoryEnd = trajectory
    ? Date.parse(trajectory.endUtc)
    : now + 86_400_000;
  const sliderValue = Math.max(
    trajectoryStart,
    Math.min(trajectoryEnd, timestamp),
  );
  const shownCount =
    previewTime === null ? visiblePositions.length : currentPosition ? 1 : 0;

  return (
    <div className="app-shell">
      <header className="topbar">
        <a href="/" className="brand" aria-label="Orbital Sentinel home">
          <span className="brand-symbol">
            <img src="/favicon.svg" width={31} height={31} alt="" />
          </span>
          <span>
            ORBITAL<span className="brand-light"> SENTINEL</span>
            <span className="brand-caption">SPACE TRAFFIC OBSERVATORY</span>
          </span>
        </a>
        <div className="topbar-divider" />
        <span className="workspace-label">Mission overview</span>
        <div className="topbar-right">
          <span
            className={`connection ${data.connection === "live" ? "connected" : ""}`}
          >
            <span className="status-dot" />
            {connectionLabel}
          </span>
          <span className="utc-clock">
            {formatUtc(now)} <span>UTC</span>
          </span>
        </div>
      </header>

      <main>
        <section className="page-heading">
          <div>
            <div className="eyebrow">EARTH ORBIT / CATALOG & PROPAGATION</div>
            <h1>Space traffic</h1>
            <p>Real orbital data. A clearer view of what’s overhead.</p>
          </div>
          <div className="heading-actions">
            <span className="source-pill">
              <span className="status-dot" />
              CELESTRAK · OMM
            </span>
            <button className="secondary-button" onClick={data.refresh}>
              <Icon name="refresh" size={15} />
              Refresh data
            </button>
          </div>
        </section>

        <section className="metrics" aria-label="Catalog status">
          <div className="metric">
            <div className="metric-icon">
              <Icon name="orbit" size={22} />
            </div>
            <div>
              <span className="metric-label">Catalog objects</span>
              <strong>
                {data.catalog.value
                  ? data.catalog.value.totalCount.toLocaleString()
                  : "—"}
              </strong>
            </div>
            <span className="metric-note">
              {data.catalog.loading
                ? "Connecting to catalog"
                : data.catalog.error
                  ? "Service unavailable"
                  : "Real orbital elements"}
            </span>
          </div>
          <div className="metric">
            <div className="metric-icon">
              <Icon name="crosshair" size={22} />
            </div>
            <div>
              <span className="metric-label">Current position samples</span>
              <strong>
                {data.positions.value
                  ? visiblePositions.length.toLocaleString()
                  : "—"}
              </strong>
            </div>
            <span className="metric-note">
              {positionError
                ? "Position feed unavailable"
                : "Predicted · not telemetry"}
            </span>
          </div>
          <div className="metric">
            <div className="metric-icon">
              <Icon name="signal" size={22} />
            </div>
            <div>
              <span className="metric-label">Latest position refresh</span>
              <strong className="metric-time">
                {data.positions.receivedAt
                  ? formatUtc(data.positions.receivedAt)
                  : "—"}
              </strong>
            </div>
            <span className="metric-note">UTC · every 15 seconds</span>
          </div>
        </section>

        <section
          className="operations-grid"
          aria-label="Space traffic workspace"
        >
          <aside className="catalog-panel panel">
            <div className="panel-heading">
              <h2>Object catalog</h2>
              <span className="count-badge">{objects.length}</span>
            </div>
            <div className="catalog-tools">
              <label className="search-field">
                <Icon name="search" size={16} />
                <input
                  aria-label="Search orbital objects"
                  placeholder="Name or NORAD ID"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                />
                {search && (
                  <button
                    aria-label="Clear search"
                    onClick={() => setSearch("")}
                  >
                    ×
                  </button>
                )}
              </label>
              <div
                className="filter-tabs"
                role="group"
                aria-label="Filter object type"
              >
                {filterOptions.map((option) => (
                  <button
                    key={option.id}
                    className={filter === option.id ? "active" : ""}
                    aria-pressed={filter === option.id}
                    onClick={() => setFilter(option.id)}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </div>
            <div
              className="catalog-list"
              aria-label="Orbital objects"
              aria-busy={data.catalog.loading}
            >
              {data.catalog.loading && (
                <div className="empty-state">
                  <span className="loading-ring" />
                  <h3>Loading catalog</h3>
                  <p>Retrieving orbital elements from the service.</p>
                </div>
              )}
              {data.catalog.error && (
                <div className="catalog-error" role="alert">
                  <Icon name="info" />
                  <div>
                    <strong>
                      {objects.length
                        ? "Catalog refresh failed"
                        : "Catalog unavailable"}
                    </strong>
                    <p>{data.catalog.error}</p>
                    <button className="text-button" onClick={data.refresh}>
                      Try again <Icon name="arrow" size={14} />
                    </button>
                  </div>
                </div>
              )}
              {!data.catalog.loading &&
                !data.catalog.error &&
                objects.length === 0 && (
                  <div className="empty-state">
                    <Icon name="orbit" size={36} />
                    <h3>Awaiting orbital data</h3>
                    <p>
                      Objects appear here after the first successful CelesTrak
                      ingestion.
                    </p>
                  </div>
                )}
              {!data.catalog.loading &&
                objects.length > 0 &&
                filtered.length === 0 && (
                  <div className="empty-state">
                    <Icon name="search" size={28} />
                    <h3>No matching objects</h3>
                    <p>Try another name, identifier, or object type.</p>
                    <button
                      className="text-button"
                      onClick={() => {
                        setSearch("");
                        setFilter("all");
                      }}
                    >
                      Clear filters
                    </button>
                  </div>
                )}
              {filtered.map((item) => {
                const kind = objectKind(item.object.objectType);
                return (
                  <button
                    key={item.object.id}
                    className={`object-row ${selectedId === item.object.id ? "selected" : ""}`}
                    onClick={() => selectObject(item.object.id)}
                    aria-pressed={selectedId === item.object.id}
                  >
                    <span className={`object-marker ${kind}`}>
                      <Icon
                        name={kind === "payload" ? "orbit" : "layers"}
                        size={17}
                      />
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
            </div>
            <div className="catalog-footer">
              <span>
                {filtered.length} of {data.catalog.value?.totalCount ?? 0}{" "}
                objects
              </span>
              <span>OMM source</span>
            </div>
            {(data.catalog.value?.totalCount ?? 0) > 200 && (
              <p className="limit-note">
                This view displays the first 200 objects. Search filters the
                loaded catalog.
              </p>
            )}
          </aside>

          <section
            className="globe-panel panel"
            aria-label="Orbital visualization"
          >
            <div className="globe-topline">
              <div>
                <span className="eyebrow">ORBITAL VIEW</span>
                <span className="globe-mode">
                  <span
                    className={`status-dot ${previewTime !== null ? "amber" : ""}`}
                  />
                  {previewTime !== null
                    ? "PREDICTION PREVIEW"
                    : "CURRENT PREDICTION"}
                </span>
              </div>
              <span className="coordinate-label">EARTH-FIXED / METRES</span>
            </div>
            <Globe
              objects={objects}
              positions={data.positions.value ?? []}
              selected={selected}
              trajectory={trajectory}
              timestamp={timestamp}
              preview={previewTime !== null}
              showTrajectory={showTrajectory}
              onSelect={selectObject}
            />
            {positionError && (
              <div className="position-warning" role="status">
                Position refresh failed. Samples older than 2 minutes are
                hidden.
              </div>
            )}
            <div className="globe-legend">
              <span>
                <i className="legend-dot payload" />
                Payload
              </span>
              <span>
                <i className="legend-dot debris" />
                Debris
              </span>
              <span>
                <i className="legend-dot rocket" />
                Rocket body
              </span>
              {objects.some(
                (item) => objectKind(item.object.objectType) === "unknown",
              ) && (
                <span>
                  <i className="legend-dot unknown" />
                  Unclassified
                </span>
              )}
            </div>
            <div className="view-summary">
              <span>
                {shownCount}{" "}
                {previewTime !== null ? "selected object" : "position samples"}
              </span>
              <span>Drag to rotate · scroll to zoom</span>
            </div>
            <div className="timeline">
              <div className="timeline-heading">
                <span>
                  <Icon name="clock" size={15} />
                  <strong>{formatUtc(timestamp)}</strong>
                  <span>UTC</span>
                </span>
                <button
                  className={`live-button ${previewTime === null ? "active" : ""}`}
                  onClick={() => setPreviewTime(null)}
                >
                  <span className="status-dot" />
                  Now
                </button>
              </div>
              <input
                className="time-slider"
                type="range"
                aria-label="Preview selected object's predicted position in UTC"
                disabled={!trajectory}
                min={trajectoryStart}
                max={trajectoryEnd}
                step={1000}
                value={sliderValue}
                onChange={(event) => setPreviewTime(Number(event.target.value))}
              />
              <div className="timeline-labels">
                <span>
                  {trajectory
                    ? `${formatUtc(trajectory.startUtc, true)} UTC`
                    : "Select an object with a prediction"}
                </span>
                <span>
                  {trajectory
                    ? `${formatUtc(trajectory.endUtc, true)} UTC`
                    : "Prediction timeline"}
                </span>
              </div>
              {previewTime !== null && (
                <div className="preview-note">
                  Preview shows the selected object only. Other objects return
                  in Now mode.
                </div>
              )}
            </div>
          </section>

          <aside
            className="details-panel panel"
            aria-label="Selected object details"
          >
            <div className="panel-heading">
              <h2>Object details</h2>
              <Icon name="crosshair" size={17} />
            </div>
            {selected ? (
              <>
                <div className="selected-summary">
                  <span
                    className={`type-label ${objectKind(selected.object.objectType)}`}
                  >
                    {kindLabel[objectKind(selected.object.objectType)]}
                  </span>
                  <h2>{selected.object.name}</h2>
                  <span className="selected-identifiers">
                    {selected.object.internationalDesignator ??
                      "No international designator"}
                    <span className="text-dot">·</span>REAL DATA
                  </span>
                </div>
                <dl className="detail-grid">
                  <div>
                    <dt>NORAD catalog ID</dt>
                    <dd>{selected.object.noradCatalogId ?? "Not assigned"}</dd>
                  </div>
                  <div>
                    <dt>Element revision</dt>
                    <dd>v{selected.version}</dd>
                  </div>
                  <div>
                    <dt>Inclination</dt>
                    <dd>
                      {selected.elements.inclinationDegrees.toFixed(2)}
                      <span>°</span>
                    </dd>
                  </div>
                  <div>
                    <dt>Orbital period</dt>
                    <dd>
                      {(
                        1440 / selected.elements.meanMotionRevolutionsPerDay
                      ).toFixed(1)}
                      <span> min</span>
                    </dd>
                  </div>
                  <div>
                    <dt>Eccentricity</dt>
                    <dd>{selected.elements.eccentricity.toFixed(6)}</dd>
                  </div>
                  <div>
                    <dt>Mean motion</dt>
                    <dd>
                      {selected.elements.meanMotionRevolutionsPerDay.toFixed(2)}
                      <span> rev/d</span>
                    </dd>
                  </div>
                </dl>
                <div className="data-freshness">
                  <div>
                    <span>Element epoch</span>
                    <span
                      className={age?.stale ? "age-label stale" : "age-label"}
                    >
                      {age?.label}
                    </span>
                  </div>
                  <strong>
                    {formatUtc(selected.elements.epochUtc, true)} UTC
                  </strong>
                  <p>
                    {age?.stale
                      ? "Elements are over 48 hours old. Prediction accuracy may be reduced."
                      : "Epoch is the reference time of the orbital elements, not the ingestion time."}
                  </p>
                </div>
                <div className="prediction-section">
                  <div className="subheading">
                    <h3>Trajectory prediction</h3>
                    <span className="small-tag">SGP4</span>
                  </div>
                  {trajectoryState.loading && (
                    <p className="prediction-status">
                      <span className="loading-ring small" />
                      Loading prediction…
                    </p>
                  )}
                  {trajectoryState.pending && (
                    <div className="notice">
                      <Icon name="clock" size={16} />
                      <p>
                        Prediction is being prepared. This view updates when
                        processing completes.
                      </p>
                    </div>
                  )}
                  {trajectoryState.error && (
                    <div className="notice warning" role="status">
                      <Icon name="info" size={16} />
                      <p>{trajectoryState.error}</p>
                    </div>
                  )}
                  {trajectory && (
                    <>
                      <div className="prediction-meta">
                        <div>
                          <strong>
                            {(
                              (trajectoryEnd - trajectoryStart) /
                              3_600_000
                            ).toFixed(0)}
                            <span> h</span>
                          </strong>
                          <span>Prediction window</span>
                        </div>
                        <div>
                          <strong>
                            {trajectory.stepSeconds}
                            <span> s</span>
                          </strong>
                          <span>Sample interval</span>
                        </div>
                      </div>
                      <label className="toggle-row">
                        <span>
                          <i className="line-swatch" />
                          Show predicted path
                        </span>
                        <input
                          type="checkbox"
                          checked={showTrajectory}
                          onChange={(event) =>
                            setShowTrajectory(event.target.checked)
                          }
                        />
                        <span className="toggle-track" aria-hidden="true" />
                      </label>
                      <p className="prediction-help">
                        Path shows up to one orbit ahead (maximum 3 hours) in
                        Earth-fixed coordinates.
                      </p>
                      {predictionExpired && (
                        <p className="notice warning">
                          The current prediction has expired. Awaiting fresh
                          propagation.
                        </p>
                      )}
                      {!currentPosition && !predictionExpired && (
                        <p className="notice warning">
                          The displayed time is outside this prediction. Use the
                          timeline to inspect its available window.
                        </p>
                      )}
                    </>
                  )}
                </div>
                <div className="source-note">
                  <Icon name="info" size={15} />
                  <p>
                    Public orbital elements and simplified frame conversion. For
                    exploration and engineering demonstrations.
                  </p>
                </div>
              </>
            ) : (
              <div className="empty-state details-empty">
                <Icon name="crosshair" size={34} />
                <h3>Your orbital perspective</h3>
                <p>
                  Select an object from the catalog or globe to inspect its
                  elements and predicted path.
                </p>
              </div>
            )}
          </aside>
        </section>
      </main>
      <footer className="page-footer">
        <div className="footer-ownership">
          <p>
            <strong>Orbital Sentinel ©</strong> 2026 All Rights Reserved.
          </p>
          <p>Created by Igor Couto.</p>
        </div>
        <nav className="footer-links" aria-label="Site information">
          <a
            href="#about"
            aria-haspopup="dialog"
            onClick={(event) => {
              event.preventDefault();
              about.current?.showModal();
            }}
          >
            About
          </a>
          <a
            href="#privacy"
            aria-haspopup="dialog"
            onClick={(event) => {
              event.preventDefault();
              privacy.current?.showModal();
            }}
          >
            Privacy
          </a>
        </nav>
        <span className="footer-warning">
          Not for operational spacecraft decisions. This is just a personal project for fun :)
        </span>
      </footer>

      <dialog
        id="about"
        ref={about}
        className="notes-dialog"
        aria-labelledby="about-title"
        onClick={(event) => {
          if (event.target === about.current) about.current?.close();
        }}
      >
        <div className="dialog-heading">
          <div>
            <div className="eyebrow">PROJECT NOTES</div>
            <h2 id="about-title">About Orbital Sentinel</h2>
          </div>
          <button
            className="icon-button close-button"
            aria-label="Close About"
            onClick={() => about.current?.close()}
          >
            ×
          </button>
        </div>
        <div className="notes-body">
          <p>
            I’m Igor Couto, and I created Orbital Sentinel to learn and have fun
            building something around my interest in space and maps.
          </p>
          <a
            href="https://github.com/igor-couto/orbital-sentinel"
            target="_blank"
            rel="noopener noreferrer"
          >
            View the project on GitHub <Icon name="external" size={13} />
          </a>
          <h3>Real inputs, predicted positions</h3>
          <p>
            CelesTrak OMM orbital elements are ingested by the backend. SGP4
            predictions are converted to Earth-fixed coordinates there. This
            browser only interpolates those samples for visualization; it does
            not calculate orbital dynamics.
          </p>
          <h3>Time is part of the data</h3>
          <p>
            Catalog positions refresh every 15 seconds and are hidden if their
            sample time differs from the current time by more than two minutes.
            The selected object moves between backend samples. Preview mode
            hides the rest of the catalog so positions from different times are
            not presented as a shared scene.
          </p>
          <h3>A deliberate first increment</h3>
          <p>
            Catalog ingestion, asynchronous propagation, and visualization are
            implemented. Conjunction detection, explainable risk assessment,
            history, alerts, deterministic scenarios, and simplified manoeuvres
            are future increments. No conjunction or risk numbers are fabricated
            in this view.
          </p>
          <h3>Scientific limitations</h3>
          <p>
            Public general-perturbation elements have limited accuracy.
            Prediction quality degrades with element age. This demonstrator has
            no operational uncertainty model and must not be used for spacecraft
            collision-avoidance decisions.
          </p>
          <a
            href="https://celestrak.org/NORAD/documentation/gp-data-formats.php"
            target="_blank"
            rel="noreferrer"
          >
            About CelesTrak GP data <Icon name="external" size={13} />
          </a>
        </div>
      </dialog>
      <dialog
        id="privacy"
        ref={privacy}
        className="notes-dialog"
        aria-labelledby="privacy-title"
        onClick={(event) => {
          if (event.target === privacy.current) privacy.current?.close();
        }}
      >
        <div className="dialog-heading">
          <div>
            <div className="eyebrow">YOUR PRIVACY</div>
            <h2 id="privacy-title">Privacy</h2>
          </div>
          <button
            className="icon-button close-button"
            aria-label="Close Privacy"
            onClick={() => privacy.current?.close()}
          >
            ×
          </button>
        </div>
        <div className="notes-body">
          <h3>No cookies or tracking</h3>
          <p>
            Orbital Sentinel does not use cookies, analytics, advertising
            trackers, or tracking pixels. You do not need an account to use the
            dashboard.
          </p>
          <h3>Your dashboard choices</h3>
          <p>
            Search terms, selected objects, and timeline settings stay in memory
            while the page is open. We do not save these preferences in cookies
            or browser storage. Your browser may cache application files and
            Earth imagery to load the dashboard faster.
          </p>
          <h3>Service logs</h3>
          <p>
            The server keeps basic request and error logs to operate and
            troubleshoot the service. These may include your IP address,
            requested URL, browser details, and request time. They are not used
            for advertising or profiling.
          </p>
          <h3>External links</h3>
          <p>
            If you follow a link to another website, such as CelesTrak, that
            website has its own privacy practices.
          </p>
        </div>
      </dialog>
    </div>
  );
}
