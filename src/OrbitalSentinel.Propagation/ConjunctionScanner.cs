using System.Diagnostics;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.Propagation;

/// <summary>
/// Common-time TEME screening followed by direct-propagation refinement. The grid lives only
/// for this scan. Curvature screening assumes the configured bound on relative acceleration;
/// subdivision assumes smooth orbital motion at that scale, not arbitrary oscillatory signals.
/// </summary>
public sealed class ConjunctionScanner : IConjunctionScanner
{
    public const string AlgorithmVersion = ConjunctionAlgorithms.Current;
    public const int MaximumObjects = 200;
    public const int MaximumGridPoints = 10_001;
    private const double NearZeroSpeedSquared = 1e-18;
    private const double FlatDistanceToleranceMeters = 0.001;
    private const double FlatRadialSpeedToleranceMetersPerSecond = 1e-6;

    public ConjunctionScanResult Scan(PredictionRunContext run, IOrbitPropagator propagator,
        CancellationToken cancellationToken = default)
    {
        ValidateRun(run);
        ArgumentNullException.ThrowIfNull(propagator);
        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var refinementTicks = 0L;
        var objects = run.Objects.OrderBy(orbit => orbit.Object.Id).ToArray();
        var times = GridTimes(run);
        var evaluators = objects.Select(propagator.CreateEvaluator).ToArray();
        var states = new TemeStateSi[objects.Length][];
        for (var index = 0; index < objects.Length; index++)
        {
            states[index] = new TemeStateSi[times.Length];
            for (var timeIndex = 0; timeIndex < times.Length; timeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                states[index][timeIndex] = InSi(evaluators[index].Evaluate(times[timeIndex]), times[timeIndex]);
            }
        }

        var results = new List<RefinedConjunction>();
        long pairs = 0, intervals = 0, candidates = 0;
        for (var primaryIndex = 0; primaryIndex < objects.Length; primaryIndex++)
        for (var secondaryIndex = primaryIndex + 1; secondaryIndex < objects.Length; secondaryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pairs++;
            var candidateIntervals = new List<int>();
            for (var interval = 0; interval < times.Length - 1; interval++)
            {
                if (interval % 64 == 0) cancellationToken.ThrowIfCancellationRequested();
                intervals++;
                var first = Sample(states[primaryIndex][interval], states[secondaryIndex][interval]);
                var last = Sample(states[primaryIndex][interval + 1], states[secondaryIndex][interval + 1]);
                var duration = (times[interval + 1] - times[interval]).TotalSeconds;
                var estimate = LinearEstimate(first.RelativePosition, first.RelativeVelocity, duration);
                // Bound departure from the start tangent, then also check the end tangent and
                // endpoint distances. 25 m/s² is conservative for the sum of two Earth-orbit
                // accelerations outside Earth; this is an engineering guard, not covariance.
                var allowance = 0.5 * run.Settings.RelativeAccelerationGuardMetersPerSecondSquared * duration * duration;
                var reverseEstimate = LinearEstimate(last.RelativePosition, -last.RelativeVelocity, duration);
                if (Math.Min(estimate.Distance, reverseEstimate.Distance) > run.Settings.CandidateDistanceMeters + allowance &&
                    Math.Min(first.Distance, last.Distance) > run.Settings.CandidateDistanceMeters)
                    continue;
                candidateIntervals.Add(interval);
                candidates++;
            }
            if (candidateIntervals.Count == 0) continue;

            var refinementStart = Stopwatch.GetTimestamp();
            var pair = new PairEvaluator(evaluators[primaryIndex], evaluators[secondaryIndex], cancellationToken);
            foreach (var index in candidateIntervals)
            {
                pair.Seed(times[index], states[primaryIndex][index], states[secondaryIndex][index]);
                pair.Seed(times[index + 1], states[primaryIndex][index + 1], states[secondaryIndex][index + 1]);
            }
            var minima = new List<(DateTimeOffset Time, bool Extended)>();
            // Connected candidate intervals are refined together; artificial sample boundaries
            // cannot become separate encounters. Separate orbital passes remain distinct minima.
            for (var cursor = 0; cursor < candidateIntervals.Count;)
            {
                var firstIndex = candidateIntervals[cursor];
                var lastIndex = firstIndex;
                while (++cursor < candidateIntervals.Count && candidateIntervals[cursor] == lastIndex + 1)
                    lastIndex = candidateIntervals[cursor];
                RefineRegion(pair, times[firstIndex], times[lastIndex + 1], run, minima);
            }

            var accepted = new List<RefinedConjunction>();
            foreach (var minimum in minima.OrderBy(value => value.Time))
            {
                var sample = pair.At(minimum.Time);
                if (sample.Distance > run.Settings.ReportingDistanceMeters) continue;
                var result = new RefinedConjunction(objects[primaryIndex].Object.Id, objects[secondaryIndex].Object.Id,
                    minimum.Time, sample.Distance, sample.RelativeVelocity.Length,
                    sample.Primary, sample.Secondary,
                    minimum.Time == run.PredictionStartUtc || minimum.Time == run.PredictionEndUtc,
                    minimum.Extended);
                if (accepted.Count > 0 &&
                    (result.TimeOfClosestApproachUtc - accepted[^1].TimeOfClosestApproachUtc).TotalSeconds <= run.Settings.TimeToleranceSeconds * 4)
                {
                    if (result.MissDistanceMeters < accepted[^1].MissDistanceMeters)
                        accepted[^1] = result;
                }
                else accepted.Add(result);
            }
            results.AddRange(accepted);
            refinementTicks += Stopwatch.GetTimestamp() - refinementStart;
        }
        watch.Stop();
        return new ConjunctionScanResult(results.OrderBy(value => value.TimeOfClosestApproachUtc)
                .ThenBy(value => value.PrimaryObjectId).ThenBy(value => value.SecondaryObjectId).ToArray(),
            new ConjunctionScanMetrics(objects.Length, pairs, intervals, candidates, results.Count,
                watch.Elapsed, TimeSpan.FromSeconds((double)refinementTicks / Stopwatch.Frequency)));
    }

    /// <summary>Constant-relative-velocity screening estimate in explicit SI units.</summary>
    public static (double SecondsFromStart, double MissDistanceMeters) EstimateLinearClosestApproach(
        CartesianVector relativePositionMeters, CartesianVector relativeVelocityMetersPerSecond, double durationSeconds)
    {
        if (!Finite(relativePositionMeters) || !Finite(relativeVelocityMetersPerSecond) ||
            !double.IsFinite(durationSeconds) || durationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "A finite state and nonnegative interval are required.");
        var result = LinearEstimate(Vector.From(relativePositionMeters), Vector.From(relativeVelocityMetersPerSecond), durationSeconds);
        return (result.Seconds, result.Distance);
    }

    private static (double Seconds, double Distance) LinearEstimate(Vector r, Vector v, double duration)
    {
        var speedSquared = v.SquaredLength;
        var seconds = speedSquared <= NearZeroSpeedSquared ? 0 : Math.Clamp(-Vector.Dot(r, v) / speedSquared, 0, duration);
        return (seconds, (r + v * seconds).Length);
    }

    private static void RefineRegion(PairEvaluator pair, DateTimeOffset start, DateTimeOffset end,
        PredictionRunContext run, List<(DateTimeOffset Time, bool Extended)> minima)
    {
        var count = (int)Math.Ceiling((end - start).TotalSeconds / run.Settings.RefinementSubdivisionSeconds);
        var samples = new PairSample[count * 2 + 1];
        var times = new DateTimeOffset[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            times[index] = start.AddTicks((end - start).Ticks * index / (samples.Length - 1));
            samples[index] = pair.At(times[index]);
        }
        // A constant separation is an extended proximity episode, not one new encounter per
        // sample. Verify both distance and radial speed throughout the subdivision grid.
        if (samples.All(sample => Math.Abs(sample.Distance - samples[0].Distance) <= FlatDistanceToleranceMeters &&
                                  Math.Abs(sample.RadialDerivative) <= Math.Max(1, sample.Distance) * FlatRadialSpeedToleranceMetersPerSecond))
        {
            minima.Add((start, true));
            return;
        }

        // Only prediction-window endpoints are eligible as one-sided minima. Region and grid
        // boundaries are not physical encounter identities.
        if (start == run.PredictionStartUtc && samples[0].RadialDerivative >= 0 && samples[0].Distance <= samples[1].Distance)
            minima.Add((start, false));
        if (end == run.PredictionEndUtc && samples[^1].RadialDerivative <= 0 && samples[^1].Distance <= samples[^2].Distance)
            minima.Add((end, false));

        for (var index = 0; index < samples.Length - 1; index++)
        {
            var left = samples[index];
            var right = samples[index + 1];
            // d(distance²)/dt = 2 r·v, using SGP4 velocity rather than finite differences.
            // A negative-to-positive crossing brackets an interior minimum. Splitting each
            // region catches separate minima instead of minimizing an entire orbit as unimodal.
            if (left.RadialDerivative > 0 || right.RadialDerivative < 0 ||
                (left.RadialDerivative == 0 && right.RadialDerivative == 0)) continue;
            var leftTime = times[index];
            var seconds = MinimizeBrent(
                offset => pair.At(leftTime.AddSeconds(offset)).DistanceSquared,
                (times[index + 1] - leftTime).TotalSeconds,
                run.Settings.TimeToleranceSeconds, run.Settings.MaximumRefinementIterations);
            var time = leftTime.AddSeconds(seconds);
            // Evaluating endpoints explicitly preserves exact grid-boundary minima.
            var best = pair.At(time).DistanceSquared;
            if (left.DistanceSquared <= best) { time = leftTime; best = left.DistanceSquared; }
            if (right.DistanceSquared < best) time = times[index + 1];
            minima.Add((time, false));
        }
    }

    /// <summary>Bounded Brent minimization: safeguarded parabolic steps, golden-section fallback.</summary>
    private static double MinimizeBrent(Func<double, double> objective, double duration, double tolerance, int maximumIterations)
    {
        const double golden = 0.3819660112501051;
        var a = 0d;
        var b = duration;
        var x = a + golden * (b - a);
        var w = x;
        var v = x;
        var fx = objective(x);
        var fw = fx;
        var fv = fx;
        var d = 0d;
        var e = 0d;
        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var middle = 0.5 * (a + b);
            var tol = tolerance / 4 + Math.Abs(x) * 1e-12;
            var tol2 = 2 * tol;
            if (Math.Abs(x - middle) <= tol2 - 0.5 * (b - a)) return x;
            var parabolicAccepted = false;
            if (Math.Abs(e) > tol)
            {
                var r = (x - w) * (fx - fv);
                var q = (x - v) * (fx - fw);
                var p = (x - v) * q - (x - w) * r;
                q = 2 * (q - r);
                if (q > 0) p = -p;
                q = Math.Abs(q);
                var previousE = e;
                e = d;
                if (q > 0 && Math.Abs(p) < Math.Abs(0.5 * q * previousE) && p > q * (a - x) && p < q * (b - x))
                {
                    d = p / q;
                    var proposed = x + d;
                    if (proposed - a < tol2 || b - proposed < tol2) d = Math.CopySign(tol, middle - x);
                    parabolicAccepted = true;
                }
            }
            if (!parabolicAccepted)
            {
                e = x >= middle ? a - x : b - x;
                d = golden * e;
            }
            var u = x + (Math.Abs(d) >= tol ? d : Math.CopySign(tol, d));
            var fu = objective(u);
            if (fu <= fx)
            {
                if (u >= x) a = x; else b = x;
                v = w; fv = fw;
                w = x; fw = fx;
                x = u; fx = fu;
            }
            else
            {
                if (u < x) a = u; else b = u;
                if (fu <= fw || w == x) { v = w; fv = fw; w = u; fw = fu; }
                else if (fu <= fv || v == x || v == w) { v = u; fv = fu; }
            }
        }
        throw new InvalidOperationException("Conjunction TCA refinement did not converge within the configured iteration limit.");
    }

    private static DateTimeOffset[] GridTimes(PredictionRunContext run)
    {
        var duration = run.PredictionEndUtc - run.PredictionStartUtc;
        var intervals = (int)Math.Ceiling(duration.TotalSeconds / run.StepSeconds);
        return Enumerable.Range(0, intervals + 1).Select(index => run.PredictionStartUtc.AddSeconds(
            Math.Min((long)index * run.StepSeconds, duration.TotalSeconds))).ToArray();
    }

    public static void ValidateRun(PredictionRunContext run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(run.Settings);
        ArgumentNullException.ThrowIfNull(run.Objects);
        run.Settings.Validate();
        if (run.Id == Guid.Empty || string.IsNullOrWhiteSpace(run.AlgorithmVersion) || string.IsNullOrWhiteSpace(run.PropagatorVersion))
            throw new ArgumentException("A run requires identity and numerical model versions.", nameof(run));
        if (run.Source is not ("Real" or "Simulation") || (run.Source == "Simulation") != run.ScenarioId.HasValue || run.ScenarioId == Guid.Empty)
            throw new ArgumentException("Run source must be Real, or Simulation with a scenario identity.", nameof(run));
        var duration = run.PredictionEndUtc - run.PredictionStartUtc;
        if (run.CreatedAtUtc == default || run.PredictionStartUtc == default || duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(48))
            throw new ArgumentOutOfRangeException(nameof(run), "A run requires a positive horizon of at most 48 hours.");
        if (run.StepSeconds is < 1 or > 300 || run.StepSeconds > duration.TotalSeconds ||
            Math.Ceiling(duration.TotalSeconds / run.StepSeconds) + 1 > MaximumGridPoints)
            throw new ArgumentOutOfRangeException(nameof(run), "Run sampling must contain at most 10,001 points, at intervals of one to 300 seconds.");
        if (run.Objects.Count > MaximumObjects || run.Objects.Any(orbit => orbit is null || orbit.Object is null || orbit.Object.Id == Guid.Empty) ||
            run.Objects.Select(orbit => orbit.Object.Id).Distinct().Count() != run.Objects.Count)
            throw new ArgumentException("A run supports at most 200 objects with distinct, nonempty identities.", nameof(run));
    }

    private static TemeStateSi InSi(StateVector state, DateTimeOffset expectedTime)
    {
        if (state.TimestampUtc != expectedTime || !Finite(state.PositionKm) || !Finite(state.VelocityKmPerSecond))
            throw new InvalidOperationException("Propagation returned a non-finite state or a state at the wrong timestamp.");
        var result = new TemeStateSi(expectedTime.ToUniversalTime(),
            new CartesianVector(state.PositionKm.X * 1000, state.PositionKm.Y * 1000, state.PositionKm.Z * 1000),
            new CartesianVector(state.VelocityKmPerSecond.X * 1000, state.VelocityKmPerSecond.Y * 1000, state.VelocityKmPerSecond.Z * 1000));
        if (!Finite(result.PositionMeters) || !Finite(result.VelocityMetersPerSecond) ||
            Vector.From(result.PositionMeters).Length > 1e12 || Vector.From(result.VelocityMetersPerSecond).Length > 1e8)
            throw new InvalidOperationException("Propagation returned an out-of-bounds state vector.");
        return result;
    }

    private static bool Finite(CartesianVector value) => value is not null &&
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static PairSample Sample(TemeStateSi primary, TemeStateSi secondary) => new(primary, secondary,
        Vector.From(secondary.PositionMeters) - Vector.From(primary.PositionMeters),
        Vector.From(secondary.VelocityMetersPerSecond) - Vector.From(primary.VelocityMetersPerSecond));

    private sealed class PairEvaluator(IOrbitEvaluator primary, IOrbitEvaluator secondary, CancellationToken cancellationToken)
    {
        private readonly Dictionary<long, PairSample> _cache = [];
        public void Seed(DateTimeOffset time, TemeStateSi a, TemeStateSi b) => _cache.TryAdd(time.UtcTicks, Sample(a, b));
        public PairSample At(DateTimeOffset time)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cache.TryGetValue(time.UtcTicks, out var cached)) return cached;
            var sample = Sample(InSi(primary.Evaluate(time), time), InSi(secondary.Evaluate(time), time));
            _cache.Add(time.UtcTicks, sample);
            return sample;
        }
    }

    private readonly record struct PairSample(TemeStateSi Primary, TemeStateSi Secondary, Vector RelativePosition, Vector RelativeVelocity)
    {
        public double DistanceSquared => RelativePosition.SquaredLength;
        public double Distance => RelativePosition.Length;
        public double RadialDerivative => Vector.Dot(RelativePosition, RelativeVelocity);
    }

    private readonly record struct Vector(double X, double Y, double Z)
    {
        public double SquaredLength => X * X + Y * Y + Z * Z;
        public double Length => Math.Sqrt(SquaredLength);
        public static Vector From(CartesianVector value) => new(value.X, value.Y, value.Z);
        public static double Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector operator -(Vector value) => new(-value.X, -value.Y, -value.Z);
        public static Vector operator *(Vector value, double factor) => new(value.X * factor, value.Y * factor, value.Z * factor);
    }
}
