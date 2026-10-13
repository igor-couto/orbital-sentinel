import { useEffect, useState } from "react";
import { getObjectDetails } from "../api/objectDetails";
import {
  constellationOperator,
  groupLabels,
  interpolateFlightMetrics,
  operationalStatus,
} from "../domain/objectDetails";
import type { ObjectDetails } from "../domain/objectDetails";
import { elementAge, kindLabel, objectKind, formatUtc } from "../domain/orbits";
import type { OrbitSnapshot, Trajectory } from "../domain/orbits";
import { ownerLabels, launchSiteLabels } from "../domain/satcatLabels";

export function ObjectFacts({
  orbit,
  trajectory,
  timestamp,
  now,
  revision,
}: {
  orbit: OrbitSnapshot;
  trajectory: Trajectory | null;
  timestamp: number;
  now: number;
  revision: number;
}) {
  const [details, setDetails] = useState<ObjectDetails | null>(null);
  const [error, setError] = useState<string | null>(null);
  const id = orbit.object.id;
  const version = orbit.version;
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    setDetails(null);
    setError(null);
    async function load() {
      try {
        const data = await getObjectDetails(id, controller.signal);
        if (!controller.signal.aborted) {
          setDetails(data);
          setError(null);
        }
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(
            cause instanceof Error
              ? cause.message
              : "Object details are unavailable.",
          );
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(load, 60_000);
      }
    }
    void load();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [id, version, revision]);
  const current =
    details?.orbitalObjectId === id && details.orbitVersion === version
      ? details
      : null;
  const meta = current?.metadata;
  const kind = objectKind(orbit.object.objectType);
  const age = elementAge(orbit.elements.epochUtc, now);
  const metrics =
    trajectory?.orbitalObjectId === id && trajectory.orbitVersion === version
      ? interpolateFlightMetrics(trajectory, timestamp)
      : null;
  const available = metrics?.altitudeKilometers != null;
  const metadataMissing = error
    ? "Unavailable"
    : current
      ? "Not provided"
      : "Loading…";
  const owner = meta?.ownerCode;
  const site = meta?.launchSiteCode;
  const ownerText = owner
    ? `${ownerLabels[owner] ?? owner}${ownerLabels[owner] ? ` (${owner})` : ""}`
    : metadataMissing;
  const siteText = site ? (launchSiteLabels[site] ?? site) : null;
  const launch =
    [meta?.launchDate?.slice(0, 10), siteText].filter(Boolean).join(" · ") ||
    metadataMissing;
  const rows = [
    ["NORAD", orbit.object.noradCatalogId ?? "Not assigned"],
    ["COSPAR", orbit.object.internationalDesignator ?? "Not assigned"],
    [
      "Operator",
      meta
        ? (constellationOperator(meta.groups, meta.objectType ?? "") ??
          "Not provided")
        : metadataMissing,
    ],
    ["Country / owner", ownerText],
    ["Launch", launch],
    [
      "Status",
      meta?.operationalStatusCode
        ? (operationalStatus[meta.operationalStatusCode] ?? "Unknown")
        : metadataMissing,
    ],
    ["Type", kindLabel[kind]],
    [
      "Altitude",
      metrics?.altitudeKilometers != null
        ? `${metrics.altitudeKilometers.toFixed(0)} km`
        : "Unavailable",
    ],
    [
      "Speed",
      metrics?.speedKilometersPerSecond != null
        ? `${metrics.speedKilometersPerSecond.toFixed(2)} km/s`
        : "Unavailable",
    ],
    [
      "Perigee / apogee",
      current
        ? `≈ ${current.meanOrbit.perigeeKilometers.toFixed(0)} / ${current.meanOrbit.apogeeKilometers.toFixed(0)} km`
        : "Unavailable",
    ],
    [
      "Period",
      `${(1440 / orbit.elements.meanMotionRevolutionsPerDay).toFixed(1)} min`,
    ],
    ["Inclination", `${orbit.elements.inclinationDegrees.toFixed(2)}°`],
    [
      "Elements epoch",
      `${orbit.elements.epochUtc.slice(0, 10)} ${formatUtc(orbit.elements.epochUtc)} UTC · ${age.label}`,
    ],
    ["Elements from", current?.elementsSource ?? "CelesTrak GP (OMM)"],
    [
      "Catalog groups",
      meta?.groups.map((g) => groupLabels[g] ?? g).join(", ") ||
        metadataMissing,
    ],
  ];
  return (
    <section
      className="object-facts-section"
      aria-label="Selected object facts"
    >
      <div className="selected-summary">
        <span className={`type-label ${kind}`}>{kindLabel[kind]}</span>
        <h2>{orbit.object.name}</h2>
        <span className="selected-identifiers">REAL ORBITAL DATA</span>
      </div>
      <div
        className={`propagation-badge ${age.stale || !available ? "stale" : ""}`}
      >
        {!available
          ? "Awaiting prediction at this time"
          : age.stale
            ? "SGP4 prediction · older orbital elements"
            : "Propagated with SGP4 from recent elements"}
      </div>
      <dl className="object-facts">
        {rows.map(([label, value]) => (
          <div key={label}>
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
      <div className="object-facts-notes">
        <p>
          Altitude and inertial speed at {formatUtc(timestamp)} UTC,
          interpolated from SGP4 samples. Perigee / apogee are approximate
          mean-orbit altitudes.
        </p>
        {meta ? (
          <p>
            <a
              href={`https://celestrak.org/satcat/records.php?CATNR=${orbit.object.noradCatalogId}`}
              target="_blank"
              rel="noreferrer"
            >
              CelesTrak SATCAT
            </a>{" "}
            · checked {meta.retrievedAtUtc.slice(0, 10)}. Groups are the lists
            imported by this project; operator is shown only when identified.
          </p>
        ) : (
          <p role="status">
            {error ??
              (current
                ? "Catalog metadata is not available yet. Orbital predictions remain available."
                : "Loading catalog metadata…")}
          </p>
        )}
        {age.stale && (
          <p>
            Elements are over 48 hours old. Prediction accuracy may be reduced.
          </p>
        )}
      </div>
      <details className="additional-elements">
        <summary>More orbital elements</summary>
        <dl className="object-facts">
          <div>
            <dt>Element revision</dt>
            <dd>v{version}</dd>
          </div>
          <div>
            <dt>Eccentricity</dt>
            <dd>{orbit.elements.eccentricity.toFixed(6)}</dd>
          </div>
          <div>
            <dt>Mean motion</dt>
            <dd>
              {orbit.elements.meanMotionRevolutionsPerDay.toFixed(2)} rev/d
            </dd>
          </div>
        </dl>
      </details>
    </section>
  );
}
