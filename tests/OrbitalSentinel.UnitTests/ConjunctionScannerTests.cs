using OrbitalSentinel.Core;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.UnitTests;

public sealed class ConjunctionScannerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid FirstId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private readonly ConjunctionScanner _scanner = new();

    [Theory]
    [InlineData(-30_000, 400, 1_000, 60, 30, 400)]
    [InlineData(30_000, 400, 1_000, 60, 0, 30002.666548158682)]
    [InlineData(-90_000, 400, 1_000, 60, 60, 30002.666548158682)]
    [InlineData(3, 4, 0, 60, 0, 5)]
    [InlineData(3, 4, 1e-10, 60, 0, 5)]
    public void LinearEstimateHandlesCrossingsClampingAndNearZeroSpeed(
        double x, double y, double vx, double duration, double expectedTime, double expectedDistance)
    {
        var result = ConjunctionScanner.EstimateLinearClosestApproach(new(x, y, 0), new(vx, 0, 0), duration);
        Assert.InRange(Math.Abs(result.SecondsFromStart - expectedTime), 0, 1e-8);
        Assert.InRange(Math.Abs(result.MissDistanceMeters - expectedDistance), 0, 1e-6);
    }

    [Fact]
    public void CrossingBetweenDistantSamplesUsesDirectStatesAndNativeVelocity()
    {
        var propagator = Crossing(31.234, 400, 10_000);
        var run = Run();
        var result = _scanner.Scan(run, propagator);
        var encounter = Assert.Single(result.Encounters);
        Assert.InRange(Math.Abs((encounter.TimeOfClosestApproachUtc - Epoch).TotalSeconds - 31.234), 0, 0.01);
        Assert.InRange(encounter.MissDistanceMeters, 399.99, 400.1);
        Assert.Equal(10_000, encounter.RelativeVelocityMetersPerSecond, 5);
        Assert.Equal(encounter.TimeOfClosestApproachUtc, encounter.PrimaryState.TimestampUtc);
        Assert.Equal(encounter.TimeOfClosestApproachUtc, encounter.SecondaryState.TimestampUtc);
        Assert.Equal(1, result.Metrics.PairCount);
        Assert.Equal(1, result.Metrics.IntervalCount);
        Assert.Equal(1, result.Metrics.CandidateCount);
        Assert.False(encounter.IsWindowBoundary);
        Assert.False(encounter.IsExtendedProximity);
        Assert.Contains(propagator.EvaluationTimes, time => time != Epoch && time != Epoch.AddSeconds(60));
    }

    [Fact]
    public void PairsAreCanonicalAndEvaluatedOnlyOnceRegardlessOfInputOrder()
    {
        var third = Snapshot(Guid.Parse("30000000-0000-0000-0000-000000000003"));
        var run = Run() with { Objects = [Snapshot(SecondId), third, Snapshot(FirstId)] };
        var result = _scanner.Scan(run, new AnalyticPropagator((id, _) =>
            (new(id == FirstId ? 0 : id == SecondId ? 100 : 200, 0, 0), new(0, 0, 0))));
        Assert.Equal(3, result.Metrics.PairCount);
        Assert.Equal(3, result.Encounters.Count);
        Assert.All(result.Encounters, encounter => Assert.True(encounter.PrimaryObjectId.CompareTo(encounter.SecondaryObjectId) < 0));
        Assert.Equal(3, result.Encounters.Select(value => (value.PrimaryObjectId, value.SecondaryObjectId)).Distinct().Count());
    }

    [Theory]
    [InlineData(10_000, 1)]
    [InlineData(10_001, 0)]
    public void ReportingThresholdIncludesEqualityAndExcludesLargerDistances(double separation, int expected)
    {
        var result = _scanner.Scan(Run(), Crossing(30, separation, 1000));
        Assert.Equal(expected, result.Encounters.Count);
    }

    [Theory]
    [InlineData(50_000, 1)]
    [InlineData(50_001, 0)]
    public void CandidateThresholdIsConfigurableAndNotTheReportingThreshold(double separation, long expectedCandidates)
    {
        var run = Run() with { Settings = new(RelativeAccelerationGuardMetersPerSecondSquared: 0) };
        var result = _scanner.Scan(run, Crossing(30, separation, 1000));
        Assert.Equal(expectedCandidates, result.Metrics.CandidateCount);
        Assert.Empty(result.Encounters);
    }

    [Fact]
    public void CurvatureAllowanceRetainsAnEncounterMissedByBothEndpointTangents()
    {
        // A circular relative path has identical tangent miss distances at both endpoints;
        // only its curved arc reaches the origin at 30 seconds.
        const double radius = 80_000;
        const double rate = Math.PI / 60;
        var propagator = new AnalyticPropagator((id, time) => id == FirstId
            ? (new(0, 0, 0), new(0, 0, 0))
            : (new(radius * Math.Sin(rate * (time - 30)), radius * (1 - Math.Cos(rate * (time - 30))), 0),
                new(radius * rate * Math.Cos(rate * (time - 30)), radius * rate * Math.Sin(rate * (time - 30)), 0)));
        var run = Run() with { Settings = new(CandidateDistanceMeters: 10_000, ReportingDistanceMeters: 1000,
            RelativeAccelerationGuardMetersPerSecondSquared: 250) };
        var result = _scanner.Scan(run, propagator);
        Assert.InRange(Assert.Single(result.Encounters).MissDistanceMeters, 0, 0.1);
        var unguarded = _scanner.Scan(run with { Settings = run.Settings with { RelativeAccelerationGuardMetersPerSecondSquared = 0 } }, propagator);
        Assert.Empty(unguarded.Encounters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void WindowEndpointsAreIncludedAndLabeled(double crossing)
    {
        var encounter = Assert.Single(_scanner.Scan(Run(), Crossing(crossing, 40, 1000)).Encounters);
        Assert.Equal(Epoch.AddSeconds(crossing), encounter.TimeOfClosestApproachUtc);
        Assert.True(encounter.IsWindowBoundary);
    }

    [Fact]
    public void SharedSampleBoundaryProducesOneEncounter()
    {
        var result = _scanner.Scan(Run(120), Crossing(60, 25, 1000));
        var encounter = Assert.Single(result.Encounters);
        Assert.Equal(Epoch.AddSeconds(60), encounter.TimeOfClosestApproachUtc);
        Assert.False(encounter.IsWindowBoundary);
    }

    [Fact]
    public void SamePairCanHaveMultipleMinimaWithinOneCoarseInterval()
    {
        const double amplitude = 100_000;
        var propagator = new AnalyticPropagator((id, time) => id == FirstId
            ? (new(0, 0, 0), new(0, 0, 0))
            : (new(amplitude * Math.Cos(Math.PI * time / 30), 200, 0), new(-amplitude * Math.PI / 30 * Math.Sin(Math.PI * time / 30), 0, 0)));
        var result = _scanner.Scan(Run() with { Settings = new(RelativeAccelerationGuardMetersPerSecondSquared: 100) }, propagator);
        Assert.Equal(2, result.Encounters.Count);
        Assert.InRange((result.Encounters[0].TimeOfClosestApproachUtc - Epoch).TotalSeconds, 14.99, 15.01);
        Assert.InRange((result.Encounters[1].TimeOfClosestApproachUtc - Epoch).TotalSeconds, 44.99, 45.01);
    }

    [Fact]
    public void ExtendedConstantProximityHasOneRepresentativeInstant()
    {
        var propagator = new AnalyticPropagator((id, time) => (new(time * 1000, id == FirstId ? 0 : 400, 0), new(1000, 0, 0)));
        var encounter = Assert.Single(_scanner.Scan(Run(600), propagator).Encounters);
        Assert.Equal(Epoch, encounter.TimeOfClosestApproachUtc);
        Assert.True(encounter.IsExtendedProximity);
        Assert.Equal(400, encounter.MissDistanceMeters, 5);
    }

    [Fact]
    public void NonDivisibleWindowIncludesExactFinalEndpoint()
    {
        var result = _scanner.Scan(Run(125), Crossing(125, 400, 1000));
        Assert.Equal(Epoch.AddSeconds(125), Assert.Single(result.Encounters).TimeOfClosestApproachUtc);
        Assert.Equal(3, result.Metrics.IntervalCount);
    }

    [Fact]
    public void CancellationIsObservedDuringDirectPropagation()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var propagator = new AnalyticPropagator((_, _) =>
        {
            if (++count == 3) cancellation.Cancel();
            return (new(0, 0, 0), new(0, 0, 0));
        });
        Assert.Throws<OperationCanceledException>(() => _scanner.Scan(Run(120), propagator, cancellation.Token));
    }

    [Fact]
    public void NonFiniteStateFailsRunInsteadOfReportingNoEncounters()
    {
        var propagator = new AnalyticPropagator((_, _) => (new(double.NaN, 0, 0), new(0, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => _scanner.Scan(Run(), propagator));
    }

    [Fact]
    public void CancellationIsAlsoObservedAfterCommonGridPreparation()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var propagator = new AnalyticPropagator((id, seconds) =>
        {
            // Four initial states: two objects at both grid endpoints. The fifth evaluation
            // is inside refinement and must not run the rest of a canceled scan.
            if (++count == 5) cancellation.Cancel();
            return id == FirstId ? (new(0, 0, 0), new(0, 0, 0))
                : (new(1000 * (seconds - 30), 100, 0), new(1000, 0, 0));
        });
        Assert.Throws<OperationCanceledException>(() => _scanner.Scan(Run(), propagator, cancellation.Token));
        Assert.InRange(count, 5, 6);
    }

    [Fact]
    public void RefinementNonConvergenceFailsExplicitly()
    {
        var propagator = new AnalyticPropagator((id, seconds) => id == FirstId
            ? (new(0, 0, 0), new(0, 0, 0))
            : (new(Math.Pow(seconds - 31.234, 3), 100, 0), new(3 * Math.Pow(seconds - 31.234, 2), 0, 0)));
        var run = Run() with { Settings = new(TimeToleranceSeconds: 0.0001, MaximumRefinementIterations: 16) };
        var error = Assert.Throws<InvalidOperationException>(() => _scanner.Scan(run, propagator));
        Assert.Contains("did not converge", error.Message);
    }

    [Fact]
    public void InvalidSettingsAndDuplicateObjectsAreRejectedBeforePropagation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConjunctionSettings(CandidateDistanceMeters: double.NaN).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConjunctionSettings(CandidateDistanceMeters: 1, ReportingDistanceMeters: 2).Validate());
        Assert.Throws<ArgumentException>(() => ConjunctionScanner.ValidateRun(Run() with { Objects = [Snapshot(FirstId), Snapshot(FirstId)] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConjunctionScanner.ValidateRun(Run() with { StepSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConjunctionScanner.ValidateRun(Run(172_801)));
    }

    [Fact]
    public void ScannerOwnsOneReusableEvaluatorPerObject()
    {
        var propagator = Crossing(30, 100, 1000);
        _scanner.Scan(Run(120), propagator);
        Assert.Equal(2, propagator.EvaluatorCount);
        Assert.True(propagator.EvaluationTimes.Count > 6);
    }

    private static PredictionRunContext Run(int seconds = 60) => new(Guid.NewGuid(), Epoch, Epoch,
        Epoch.AddSeconds(seconds), 60, "Real", null, ConjunctionAlgorithms.Current, "analytic-test-v1", new(),
        [Snapshot(FirstId), Snapshot(SecondId)]);

    private static OrbitSnapshot Snapshot(Guid id) => ReferenceOrbit.Snapshot() with
    {
        Object = ReferenceOrbit.Snapshot().Object with { Id = id }
    };

    private static AnalyticPropagator Crossing(double time, double separation, double speed) => new((id, seconds) => id == FirstId
        ? (new(0, 0, 0), new(0, 0, 0))
        : (new(speed * (seconds - time), separation, 0), new(speed, 0, 0)));

    private sealed class AnalyticPropagator(Func<Guid, double, (CartesianVector PositionMeters, CartesianVector VelocityMetersPerSecond)> function) : IOrbitPropagator
    {
        public List<DateTimeOffset> EvaluationTimes { get; } = [];
        public int EvaluatorCount { get; private set; }
        public StateVector Propagate(OrbitSnapshot orbit, DateTimeOffset timestampUtc)
        {
            EvaluationTimes.Add(timestampUtc);
            var state = function(orbit.Object.Id, (timestampUtc - Epoch).TotalSeconds);
            return new(timestampUtc,
                new(state.PositionMeters.X / 1000, state.PositionMeters.Y / 1000, state.PositionMeters.Z / 1000),
                new(state.VelocityMetersPerSecond.X / 1000, state.VelocityMetersPerSecond.Y / 1000, state.VelocityMetersPerSecond.Z / 1000));
        }
        public IOrbitEvaluator CreateEvaluator(OrbitSnapshot orbit)
        {
            EvaluatorCount++;
            return new Evaluator(this, orbit);
        }
        public Trajectory Predict(OrbitSnapshot orbit, DateTimeOffset startUtc, TimeSpan horizon, TimeSpan step, CancellationToken cancellationToken) => throw new NotSupportedException();
        private sealed class Evaluator(AnalyticPropagator parent, OrbitSnapshot orbit) : IOrbitEvaluator
        {
            public StateVector Evaluate(DateTimeOffset timestampUtc) => parent.Propagate(orbit, timestampUtc);
        }
    }
}
