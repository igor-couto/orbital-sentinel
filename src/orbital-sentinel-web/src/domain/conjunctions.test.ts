import { describe, expect, it } from "vitest";
import {
  completedWindow,
  formatDistance,
  formatSpeed,
  predictionClock,
  tcaCountdown,
} from "./conjunctions";
import type { PredictionRun } from "./conjunctions";

const run: PredictionRun = {
  id: "run",
  source: "Simulation",
  status: "Completed",
  createdAtUtc: "2026-10-09T10:00:00Z",
  predictionStartUtc: "2026-01-01T00:00:00Z",
  predictionEndUtc: "2026-01-02T00:00:00Z",
  clockUtc: "2026-01-01T00:00:00Z",
  objectCount: 2,
};
describe("conjunction presentation", () => {
  it("uses a fixed virtual clock for old simulation fixtures instead of today's wall clock", () => {
    const today = Date.parse("2026-10-09T10:00:00Z");
    expect(predictionClock(run, today)).toBe(Date.parse(run.clockUtc));
    expect(
      tcaCountdown("2026-01-01T00:05:30Z", predictionClock(run, today)),
    ).toBe("In 5m 30s");
    expect(completedWindow(run, today)).toBe(true);
    expect(completedWindow({ ...run, source: "Real" }, today)).toBe(false);
  });
  it("never describes incomplete or failed runs as completed screening", () => {
    const now = Date.parse(run.clockUtc);
    expect(completedWindow(null, now)).toBe(false);
    for (const status of ["Pending", "Running", "Failed"] as const)
      expect(completedWindow({ ...run, status }, now)).toBe(false);
    expect(
      completedWindow(
        { ...run, source: "Real" },
        Date.parse(run.predictionEndUtc),
      ),
    ).toBe(false);
  });
  it("distinguishes future, exact and past TCA", () => {
    const tca = "2026-01-01T02:00:00Z";
    const time = Date.parse(tca);
    expect(tcaCountdown(tca, time - 3_630_000)).toBe("In 1h 0m");
    expect(tcaCountdown(tca, time)).toBe("At closest approach");
    expect(tcaCountdown(tca, time + 30_000)).toBe("30s ago");
  });
  it("makes distance and speed units explicit at the display conversion boundary", () => {
    expect(formatDistance(680)).toBe("680.0 m");
    expect(formatDistance(2100)).toBe("2.10 km");
    expect(formatSpeed(12345)).toBe("12.35 km/s");
    expect(formatSpeed(0)).toBe("0.0 m/s");
  });
});
