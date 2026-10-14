import { useRef } from "react";
import {
  completedWindow,
  formatDistance,
  formatSpeed,
  predictionClock,
  tcaCountdown,
} from "../domain/conjunctions";
import { formatUtc } from "../domain/orbits";
import type { ConjunctionState } from "../hooks/useConjunctions";
import { Icon } from "./Icon";

export function ConjunctionPanel({
  state,
  now,
}: {
  state: ConjunctionState;
  now: number;
}) {
  const simulation = useRef<HTMLDialogElement>(null);
  const clock = predictionClock(state.run, now);
  const validWindow = completedWindow(state.run, now);
  const upcoming =
    state.list?.items.filter(
      (item) =>
        Date.parse(item.latestAssessment.timeOfClosestApproachUtc) >= clock,
    ) ?? [];
  return (
    <>
      <div className="conjunction-tools">
        <div
          className="filter-tabs"
          role="group"
          aria-label="Conjunction source"
        >
          {(["Real", "Simulation"] as const).map((source) => (
            <button
              key={source}
              className={state.source === source ? "active" : ""}
              aria-pressed={state.source === source}
              onClick={() => state.changeSource(source)}
            >
              {source}
            </button>
          ))}
        </div>
        <button
          className="secondary-button simulation-button"
          onClick={() => simulation.current?.showModal()}
        >
          <Icon name="orbit" size={15} />
          Create simulation
        </button>
      </div>
      {state.source === "Simulation" && (
        <div className="simulation-notice">
          <strong>SIMULATION · VIRTUAL CLOCK</strong>
          <span>
            {state.run
              ? `${formatUtc(clock, true)} UTC · paused`
              : "An isolated synthetic scenario"}
          </span>
        </div>
      )}
      {(state.run?.unpropagatedObjectCount ?? 0) > 0 && (
        <div className="simulation-notice" role="status">
          <strong>Partial screening coverage</strong>
          <span>
            {state.run!.unpropagatedObjectCount} of {state.run!.objectCount}{" "}
            objects could not be propagated through the full window and were
            excluded. Results cover the remaining objects.
          </span>
        </div>
      )}
      {state.source === "Real" && validWindow && (
        <p className="history-caption" style={{ margin: "0 14px 10px" }}>
          Latest completed screening. New results appear when ready.
        </p>
      )}
      <div
        className="conjunction-list catalog-list"
        aria-label="Upcoming conjunctions"
        aria-busy={state.loading}
      >
        {state.error && (
          <div className="catalog-error" role="alert">
            <Icon name="info" />
            <div>
              <strong>Conjunction refresh failed</strong>
              <p>{state.error}</p>
              <button className="text-button" onClick={state.refresh}>
                Try again
              </button>
            </div>
          </div>
        )}
        {state.loading && (
          <div className="empty-state">
            <span className="loading-ring" />
            <h3>Loading encounters</h3>
          </div>
        )}
        {!state.loading && !state.error && !state.run && (
          <div className="empty-state">
            <Icon name="orbit" size={30} />
            <h3>
              {state.source === "Simulation"
                ? "Explore a close approach"
                : "Awaiting a prediction run"}
            </h3>
            <p>
              {state.source === "Simulation"
                ? "Create a deterministic scenario to explore the same screening and refinement process."
                : "Conjunction results appear after the full catalog has been predicted and screened."}
            </p>
          </div>
        )}
        {!state.loading &&
          !state.error &&
          state.run &&
          state.run.status !== "Completed" && (
            <div className="empty-state" role="status">
              {state.run.status !== "Failed" && (
                <span className="loading-ring" />
              )}
              <h3>
                {state.run.status === "Failed"
                  ? "Prediction run failed"
                  : "Calculating close approaches"}
              </h3>
              <p>
                {state.run.status === "Failed"
                  ? (state.run.error ??
                    "This run did not finish. No complete screening result is available.")
                  : `Screening ${state.run.objectCount} objects. Results will appear when the complete run is ready.`}
              </p>
            </div>
          )}
        {!state.loading &&
          !state.error &&
          state.run?.status === "Completed" &&
          !validWindow && (
            <div className="empty-state">
              <Icon name="clock" size={30} />
              <h3>Prediction window ended</h3>
              <p>
                Waiting for a fresh run. This does not mean the current sky has
                no close approaches.
              </p>
            </div>
          )}
        {!state.loading &&
          !state.error &&
          validWindow &&
          state.list &&
          upcoming.length === 0 && (
            <div className="empty-state">
              <Icon name="crosshair" size={30} />
              <h3>No upcoming encounters in this view</h3>
              <p>
                The completed run found no upcoming encounters on this page
                within the configured screening distance. This is not a
                collision-risk assessment.
              </p>
            </div>
          )}
        {validWindow &&
          upcoming.map((item) => (
            <button
              key={item.id}
              className={`encounter-row ${state.selected?.id === item.id ? "selected" : ""}`}
              aria-pressed={state.selected?.id === item.id}
              onClick={() => state.select(item)}
            >
              <span className={`source-tag ${item.source.toLowerCase()}`}>
                {item.source}
              </span>
              <strong>{item.primaryName}</strong>
              <span className="encounter-pair-divider">↔</span>
              <strong>{item.secondaryName}</strong>
              <span className="encounter-tca">
                {formatUtc(
                  item.latestAssessment.timeOfClosestApproachUtc,
                  true,
                )}{" "}
                UTC
              </span>
              <span className="encounter-countdown">
                {tcaCountdown(
                  item.latestAssessment.timeOfClosestApproachUtc,
                  clock,
                )}
              </span>
              <span className="encounter-numbers">
                <span>
                  <small>Miss distance</small>
                  {formatDistance(item.latestAssessment.missDistanceMeters)}
                </span>
                <span>
                  <small>Relative speed</small>
                  {formatSpeed(
                    item.latestAssessment.relativeVelocityMetersPerSecond,
                  )}
                </span>
              </span>
            </button>
          ))}
      </div>
      <div className="catalog-footer conjunction-pagination">
        <button
          aria-label="Previous encounters page"
          disabled={state.page <= 1 || state.loading}
          onClick={() => state.changePage(state.page - 1)}
        >
          Previous
        </button>
        <span>
          Page {state.page}
          {state.list ? ` · ${state.list.totalCount} encounters` : ""}
        </span>
        <button
          aria-label="Next encounters page"
          disabled={
            !state.list ||
            state.page * state.list.pageSize >= state.list.totalCount ||
            state.loading
          }
          onClick={() => state.changePage(state.page + 1)}
        >
          Next
        </button>
      </div>
      <dialog
        ref={simulation}
        className="notes-dialog"
        aria-labelledby="simulation-title"
        onClick={(event) => {
          if (event.target === simulation.current) simulation.current?.close();
        }}
      >
        <div className="dialog-heading">
          <div>
            <div className="eyebrow">DETERMINISTIC SCENARIO</div>
            <h2 id="simulation-title">Create a simulated conjunction</h2>
          </div>
          <button
            className="icon-button close-button"
            aria-label="Close simulation"
            onClick={() => simulation.current?.close()}
          >
            <Icon name="close" size={18} />
          </button>
        </div>
        <div className="notes-body">
          <p>
            Two synthetic orbital objects cross between the normal prediction
            samples. The backend screens and refines their close approach using
            the same process as real objects.
          </p>
          <p>
            This scenario is separate from the CelesTrak catalog. Its fixed
            virtual clock makes the demonstration repeatable; it does not
            represent an encounter happening today.
          </p>
          <p>No collision probability or risk classification is calculated.</p>
          {state.launchError && <p role="alert">{state.launchError}</p>}
          <button
            className="secondary-button"
            disabled={state.launching}
            onClick={() => {
              void state.launch().then((created) => {
                if (created) simulation.current?.close();
              });
            }}
          >
            {state.launching
              ? "Creating scenario…"
              : state.launchError
                ? "Retry scenario"
                : "Run simulation"}
          </button>
        </div>
      </dialog>
    </>
  );
}

export function ConjunctionDetails({
  state,
  now,
  onFocus,
}: {
  state: ConjunctionState;
  now: number;
  onFocus: () => void;
}) {
  const assessment = state.assessment;
  const clock = predictionClock(state.run, now);
  return (
    <>
      <div className="panel-heading">
        <h2>Encounter details</h2>
        <Icon name="crosshair" size={17} />
      </div>
      {!state.selected ? (
        <div className="empty-state details-empty">
          <Icon name="crosshair" size={34} />
          <h3>Explore an encounter</h3>
          <p>
            Select a conjunction to see both predicted paths, its closest
            approach, and assessment history.
          </p>
        </div>
      ) : (
        <>
          <div className="selected-summary encounter-summary">
            <span
              className={`source-tag ${state.selected.source.toLowerCase()}`}
            >
              {state.selected.source}
            </span>
            <h3>{state.selected.primaryName}</h3>
            <span className="encounter-pair-divider">↔</span>
            <h3>{state.selected.secondaryName}</h3>
          </div>
          {state.selectionError && (
            <div className="catalog-error" role="alert">
              <p>{state.selectionError}</p>
              <button className="text-button" onClick={state.refresh}>
                Retry
              </button>
            </div>
          )}
          {state.selectionLoading && (
            <div className="selection-loading" role="status">
              <span className="loading-ring" />
              Loading pinned predictions…
            </div>
          )}
          {assessment && (
            <div className="encounter-detail-body">
              <dl className="encounter-facts">
                <div>
                  <dt>Time of closest approach</dt>
                  <dd>
                    {formatUtc(assessment.timeOfClosestApproachUtc, true)} UTC
                  </dd>
                </div>
                <div>
                  <dt>
                    {state.selected.source === "Simulation"
                      ? "From virtual clock"
                      : "Countdown"}
                  </dt>
                  <dd>
                    {tcaCountdown(assessment.timeOfClosestApproachUtc, clock)}
                  </dd>
                </div>
                <div>
                  <dt>Miss distance</dt>
                  <dd>{formatDistance(assessment.missDistanceMeters)}</dd>
                </div>
                <div>
                  <dt>Relative velocity at TCA</dt>
                  <dd>
                    {formatSpeed(assessment.relativeVelocityMetersPerSecond)}
                  </dd>
                </div>
                <div>
                  <dt>Assessment calculated</dt>
                  <dd>{formatUtc(assessment.detectedAtUtc, true)} UTC</dd>
                </div>
                <div>
                  <dt>Orbital data versions · A / B</dt>
                  <dd>
                    {assessment.primaryOrbitVersion} /{" "}
                    {assessment.secondaryOrbitVersion}
                  </dd>
                </div>
                <div>
                  <dt>Prediction run</dt>
                  <dd className="run-identity">{assessment.predictionRunId}</dd>
                </div>
              </dl>
              {assessment.isWindowBoundary && (
                <p className="encounter-caveat">
                  Closest approach is at a prediction-window boundary; the true
                  minimum may lie outside this window.
                </p>
              )}
              {assessment.isExtendedProximity && (
                <p className="encounter-caveat">
                  Extended proximity: the objects remain close over an interval,
                  rather than a single isolated crossing.
                </p>
              )}
              <button
                className="secondary-button encounter-focus"
                disabled={!state.visualization}
                onClick={onFocus}
              >
                <Icon name="crosshair" size={15} />
                Focus at closest approach
              </button>
              <h3 className="history-heading">Assessment history</h3>
              <p className="history-caption">
                Earlier predictions are retained. Select one to inspect its
                original orbital data.
              </p>
              <div
                className="assessment-history"
                aria-label="Assessment history"
              >
                {state.detail?.assessments.map((item) => (
                  <button
                    key={item.id}
                    className={assessment.id === item.id ? "active" : ""}
                    aria-pressed={assessment.id === item.id}
                    onClick={() => state.selectAssessment(item)}
                  >
                    <span>{formatUtc(item.detectedAtUtc, true)} UTC</span>
                    <strong>{formatDistance(item.missDistanceMeters)}</strong>
                    <small>
                      TCA {formatUtc(item.timeOfClosestApproachUtc, true)} UTC
                    </small>
                  </button>
                ))}
              </div>
            </div>
          )}
          <div className="source-note">
            <Icon name="info" size={15} />
            <p>
              Public-element conjunction screening. Miss distance is not
              collision probability. No uncertainty model or risk classification
              is applied.
            </p>
          </div>
        </>
      )}
    </>
  );
}
