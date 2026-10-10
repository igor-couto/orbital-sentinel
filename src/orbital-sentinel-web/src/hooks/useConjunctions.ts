import { useCallback, useEffect, useRef, useState } from "react";
import {
  createSimulation,
  getConjunction,
  getConjunctions,
  getConjunctionVisualization,
  getPredictionRun,
} from "../api/conjunctions";
import { completedWindow, predictionClock } from "../domain/conjunctions";
import type {
  ConjunctionAssessment,
  ConjunctionDetail,
  ConjunctionPage,
  ConjunctionSummary,
  ConjunctionVisualization,
  PredictionRun,
  PredictionSource,
} from "../domain/conjunctions";

const message = (error: unknown) =>
  error instanceof Error
    ? error.message
    : "The conjunction service is unavailable.";

export function useConjunctions(active: boolean, revision: number) {
  const [source, setSource] = useState<PredictionSource>("Real");
  const [runId, setRunId] = useState<string | undefined>();
  const [run, setRun] = useState<PredictionRun | null>(null);
  const [page, setPage] = useState(1);
  const [list, setList] = useState<ConjunctionPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<ConjunctionSummary | null>(null);
  const [detail, setDetail] = useState<ConjunctionDetail | null>(null);
  const [assessment, setAssessment] = useState<ConjunctionAssessment | null>(
    null,
  );
  const [visualization, setVisualization] =
    useState<ConjunctionVisualization | null>(null);
  const [selectionLoading, setSelectionLoading] = useState(false);
  const [selectionError, setSelectionError] = useState<string | null>(null);
  const [launching, setLaunching] = useState(false);
  const [launchError, setLaunchError] = useState<string | null>(null);
  const launchKey = useRef<string | null>(null);
  const launchController = useRef<AbortController | null>(null);
  const listedRunId = useRef<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const refresh = useCallback(() => setRefreshKey((value) => value + 1), []);

  useEffect(() => () => launchController.current?.abort(), []);
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function load() {
      try {
        const current = await getPredictionRun(
          source,
          controller.signal,
          runId,
        );
        if (controller.signal.aborted) return;
        setRun(current);
        const changedRun = listedRunId.current !== (current?.id ?? null);
        listedRunId.current = current?.id ?? null;
        if (changedRun && page !== 1) {
          setList(null);
          setPage(1);
          return;
        }
        const results =
          current && completedWindow(current, Date.now())
            ? await getConjunctions(
                source,
                current.id,
                page,
                new Date(predictionClock(current, Date.now())).toISOString(),
                controller.signal,
              )
            : null;
        if (controller.signal.aborted) return;
        setList(results);
        setError(null);
      } catch (failure) {
        if (!controller.signal.aborted) setError(message(failure));
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
          timer = setTimeout(load, 10_000);
        }
      }
    }
    void load();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [active, source, runId, page, revision, refreshKey]);

  useEffect(() => {
    if (!selected || !active) return;
    const controller = new AbortController();
    setSelectionLoading(true);
    setSelectionError(null);
    void getConjunction(selected.id, controller.signal)
      .then((value) => {
        if (controller.signal.aborted) return;
        if (
          value.source !== selected.source ||
          value.primaryObjectId !== selected.primaryObjectId ||
          value.secondaryObjectId !== selected.secondaryObjectId
        )
          throw new Error("The service returned a different encounter.");
        setDetail(value);
        // Pin the assessment the user selected, even if a newer run completes meanwhile.
        setAssessment(
          (current) =>
            current ??
            value.assessments.find(
              (item) => item.id === selected.latestAssessment.id,
            ) ??
            selected.latestAssessment,
        );
      })
      .catch((failure) => {
        if (!controller.signal.aborted) {
          setSelectionError(message(failure));
          setSelectionLoading(false);
        }
      });
    return () => controller.abort();
  }, [selected, active, refreshKey]);

  useEffect(() => {
    if (!assessment || !selected || !active) return;
    const controller = new AbortController();
    setVisualization(null);
    setSelectionLoading(true);
    setSelectionError(null);
    void getConjunctionVisualization(assessment, selected, controller.signal)
      .then((value) => {
        if (!controller.signal.aborted) setVisualization(value);
      })
      .catch((failure) => {
        if (!controller.signal.aborted) setSelectionError(message(failure));
      })
      .finally(() => {
        if (!controller.signal.aborted) setSelectionLoading(false);
      });
    return () => controller.abort();
  }, [assessment, selected, active, refreshKey]);

  function clearSelection() {
    setSelected(null);
    setDetail(null);
    setAssessment(null);
    setVisualization(null);
    setSelectionError(null);
    setSelectionLoading(false);
  }
  function changeSource(next: PredictionSource) {
    if (next === source) return;
    setSource(next);
    setRunId(undefined);
    setRun(null);
    setList(null);
    setPage(1);
    setLoading(true);
    setError(null);
    listedRunId.current = null;
    clearSelection();
  }
  function select(item: ConjunctionSummary) {
    if (
      selected?.id === item.id &&
      selected.latestAssessment.id === item.latestAssessment.id
    )
      return;
    clearSelection();
    setSelected(item);
    setSelectionLoading(true);
  }
  function selectAssessment(item: ConjunctionAssessment) {
    if (assessment?.id === item.id) return;
    setVisualization(null);
    setAssessment(item);
    setSelectionLoading(true);
  }
  async function launch(): Promise<boolean> {
    if (launching) return false;
    setLaunching(true);
    setLaunchError(null);
    const controller = new AbortController();
    launchController.current = controller;
    launchKey.current ??= crypto.randomUUID();
    try {
      const created = await createSimulation(
        launchKey.current,
        controller.signal,
      );
      if (controller.signal.aborted) return false;
      changeSource("Simulation");
      clearSelection();
      setPage(1);
      setList(null);
      setLoading(true);
      setError(null);
      setRunId(created.id);
      setRun(created);
      launchKey.current = null;
      return true;
    } catch (failure) {
      if (!controller.signal.aborted) setLaunchError(message(failure));
      return false;
    } finally {
      if (!controller.signal.aborted) setLaunching(false);
    }
  }
  function changePage(next: number) {
    setPage(next);
    setList(null);
    setLoading(true);
  }
  return {
    source,
    run,
    page,
    list,
    loading,
    error,
    selected,
    detail,
    assessment,
    visualization,
    selectionLoading,
    selectionError,
    launching,
    launchError,
    changeSource,
    changePage,
    select,
    selectAssessment,
    clearSelection,
    launch,
    refresh,
  };
}
export type ConjunctionState = ReturnType<typeof useConjunctions>;
