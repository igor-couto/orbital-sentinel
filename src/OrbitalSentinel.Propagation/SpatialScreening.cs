using System.Diagnostics;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.Propagation;

public sealed partial class ConjunctionScanner
{
    public ConjunctionScanResult Scan(PredictionRunContext run, IOrbitPropagator propagator,
        CancellationToken cancellationToken = default)
    {
        ValidateRun(run);
        ArgumentNullException.ThrowIfNull(propagator);
        var watch = Stopwatch.StartNew();
        var times = GridTimes(run);
        var objects = run.Objects.OrderBy(o => o.Object.Id).ToArray();
        var evaluators = new IOrbitEvaluator?[objects.Length];
        var failed = new HashSet<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { evaluators[i] = propagator.CreateEvaluator(objects[i]); }
            catch (OrbitPropagationException) when (run.Source == "Real") { failed.Add(i); }
        }
        // Only adjacent grid slices are retained, rather than N × 1,441 heap-allocated states.
        var first = new TemeStateSi[objects.Length];
        var last = new TemeStateSi[objects.Length];
        var bounds = new SweptBounds[objects.Length];
        var open = new Dictionary<(int A, int B), CandidateRegion>();
        var results = new List<RefinedConjunction>();
        long candidates = 0, narrowIntervals = 0, refinementTicks = 0;
        Evaluate(times[0], first);
        for (var interval = 0; interval < times.Length - 1; interval++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Evaluate(times[interval + 1], last);
            var duration = (times[interval + 1] - times[interval]).TotalSeconds;
            var allowance = 0.5 * run.Settings.RelativeAccelerationGuardMetersPerSecondSquared * duration * duration;
            var distance = run.Settings.CandidateDistanceMeters + allowance;
            for (var i = 0; i < objects.Length; i++)
                if (!failed.Contains(i)) bounds[i] = SweptBounds.Create(first[i], last[i], duration, distance / 2);

            foreach (var (a, b) in OverlappingPairs(bounds, failed, cancellationToken))
            {
                narrowIntervals++;
                var start = Sample(first[a], first[b]);
                var end = Sample(last[a], last[b]);
                if (Math.Min(LinearEstimate(start.RelativePosition, start.RelativeVelocity, duration).Distance,
                        LinearEstimate(end.RelativePosition, -end.RelativeVelocity, duration).Distance) > distance &&
                    Math.Min(start.Distance, end.Distance) > run.Settings.CandidateDistanceMeters) continue;
                candidates++;
                var key = (a, b);
                if (!open.TryGetValue(key, out var region))
                    open[key] = region = new CandidateRegion(interval, first[a], first[b]);
                region.End = interval + 1;
                region.LastA = last[a]; region.LastB = last[b];
            }
            foreach (var key in open.Keys.ToArray())
            {
                var region = open[key];
                if (failed.Contains(key.A) || failed.Contains(key.B)) { open.Remove(key); continue; }
                if (region.End == interval + 1 && interval < times.Length - 2) continue;
                Refine(key.A, key.B, region);
                open.Remove(key);
            }
            (first, last) = (last, first);
        }
        var excludedIds = failed.Select(i => objects[i].Object.Id).ToHashSet();
        results.RemoveAll(r => excludedIds.Contains(r.PrimaryObjectId) || excludedIds.Contains(r.SecondaryObjectId));
        if (objects.Length >= 2 && objects.Length - failed.Count < 2)
            throw new InvalidOperationException("Fewer than two objects could propagate through the screening window.");
        var pairs = (long)objects.Length * (objects.Length - 1) / 2;
        watch.Stop();
        return new(results.OrderBy(r => r.TimeOfClosestApproachUtc).ThenBy(r => r.PrimaryObjectId)
            .ThenBy(r => r.SecondaryObjectId).ToArray(), new(objects.Length, pairs, pairs * (times.Length - 1),
            candidates, results.Count, watch.Elapsed, TimeSpan.FromSeconds((double)refinementTicks / Stopwatch.Frequency),
            narrowIntervals, failed.Count));

        void Evaluate(DateTimeOffset time, TemeStateSi[] output)
        {
            for (var i = 0; i < objects.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failed.Contains(i)) continue;
                try { output[i] = InSi(evaluators[i]!.Evaluate(time), time); }
                catch (OrbitPropagationException) when (run.Source == "Real") { failed.Add(i); }
            }
        }
        void Refine(int a, int b, CandidateRegion region)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            try
            {
                var pair = new PairEvaluator(evaluators[a]!, evaluators[b]!, cancellationToken);
                pair.Seed(times[region.Start], region.FirstA, region.FirstB);
                pair.Seed(times[region.End], region.LastA!, region.LastB!);
                var minima = new List<(DateTimeOffset Time, bool Extended)>();
                RefineRegion(pair, times[region.Start], times[region.End], run, minima);
                RefinedConjunction? previous = null;
                foreach (var minimum in minima.OrderBy(m => m.Time))
                {
                    var sample = pair.At(minimum.Time);
                    if (sample.Distance > run.Settings.ReportingDistanceMeters) continue;
                    var item = new RefinedConjunction(objects[a].Object.Id, objects[b].Object.Id, minimum.Time,
                        sample.Distance, sample.RelativeVelocity.Length, sample.Primary, sample.Secondary,
                        minimum.Time == run.PredictionStartUtc || minimum.Time == run.PredictionEndUtc, minimum.Extended);
                    if (previous is not null && (item.TimeOfClosestApproachUtc - previous.TimeOfClosestApproachUtc).TotalSeconds
                        <= run.Settings.TimeToleranceSeconds * 4)
                    {
                        if (item.MissDistanceMeters < previous.MissDistanceMeters) results[^1] = previous = item;
                    }
                    else { results.Add(item); previous = item; }
                }
            }
            catch (OrbitPropagationException ex) when (run.Source == "Real")
            {
                failed.Add(ex.OrbitalObjectId == objects[a].Object.Id ? a : b);
            }
            refinementTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    private sealed class CandidateRegion(int start, TemeStateSi a, TemeStateSi b)
    {
        public int Start { get; } = start;
        public int End { get; set; }
        public TemeStateSi FirstA { get; } = a;
        public TemeStateSi FirstB { get; } = b;
        public TemeStateSi? LastA { get; set; }
        public TemeStateSi? LastB { get; set; }
    }

    private const double CellSize = 500_000;
    private readonly record struct Cell(int X, int Y, int Z);
    private readonly record struct SweptBounds(Vector Min, Vector Max)
    {
        // Union BOTH tangent segments, inflated by half the pairwise screening distance.
        // Any pair accepted by either tangent or endpoint test must overlap these boxes.
        public static SweptBounds Create(TemeStateSi a, TemeStateSi b, double seconds, double padding)
        {
            var p = Vector.From(a.PositionMeters); var q = Vector.From(b.PositionMeters);
            var forward = p + Vector.From(a.VelocityMetersPerSecond) * seconds;
            var reverse = q - Vector.From(b.VelocityMetersPerSecond) * seconds;
            return new(new(Math.Min(Math.Min(p.X, q.X), Math.Min(forward.X, reverse.X)) - padding,
                           Math.Min(Math.Min(p.Y, q.Y), Math.Min(forward.Y, reverse.Y)) - padding,
                           Math.Min(Math.Min(p.Z, q.Z), Math.Min(forward.Z, reverse.Z)) - padding),
                       new(Math.Max(Math.Max(p.X, q.X), Math.Max(forward.X, reverse.X)) + padding,
                           Math.Max(Math.Max(p.Y, q.Y), Math.Max(forward.Y, reverse.Y)) + padding,
                           Math.Max(Math.Max(p.Z, q.Z), Math.Max(forward.Z, reverse.Z)) + padding));
        }
        public Cell Low => new((int)Math.Floor(Min.X / CellSize), (int)Math.Floor(Min.Y / CellSize), (int)Math.Floor(Min.Z / CellSize));
        public Cell High => new((int)Math.Floor(Max.X / CellSize), (int)Math.Floor(Max.Y / CellSize), (int)Math.Floor(Max.Z / CellSize));
        public bool Overlaps(SweptBounds b) => Min.X <= b.Max.X && Max.X >= b.Min.X &&
            Min.Y <= b.Max.Y && Max.Y >= b.Min.Y && Min.Z <= b.Max.Z && Max.Z >= b.Min.Z;
    }

    private static IEnumerable<(int A, int B)> OverlappingPairs(SweptBounds[] boxes, HashSet<int> failed, CancellationToken ct)
    {
        var cells = new Dictionary<Cell, List<int>>();
        var large = new HashSet<int>();
        for (var i = 0; i < boxes.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (failed.Contains(i)) continue;
            var box = boxes[i]; var low = box.Low; var high = box.High;
            if ((double)(high.X - low.X + 1) * (high.Y - low.Y + 1) * (high.Z - low.Z + 1) > 64)
            { large.Add(i); continue; }
            for (var x = low.X; x <= high.X; x++)
            for (var y = low.Y; y <= high.Y; y++)
            for (var z = low.Z; z <= high.Z; z++)
            {
                var cell = new Cell(x, y, z);
                if (!cells.TryGetValue(cell, out var members)) cells[cell] = members = [];
                foreach (var j in members)
                {
                    ct.ThrowIfCancellationRequested();
                    var other = boxes[j]; var otherLow = other.Low;
                    // Process overlapping boxes once, in their first shared cell.
                    if (x != Math.Max(low.X, otherLow.X) || y != Math.Max(low.Y, otherLow.Y)
                        || z != Math.Max(low.Z, otherLow.Z) || !box.Overlaps(other)) continue;
                    yield return (j, i);
                }
                members.Add(i);
            }
        }
        // Unusually long tangent segments still get complete screening, without allocating
        // millions of grid cells. Cancellation bounds the fallback as well.
        foreach (var i in large)
        for (var j = 0; j < boxes.Length; j++)
        {
            ct.ThrowIfCancellationRequested();
            if (i == j || failed.Contains(j) || (large.Contains(j) && j < i)) continue;
            if (boxes[i].Overlaps(boxes[j])) yield return (Math.Min(i, j), Math.Max(i, j));
        }
    }
}
