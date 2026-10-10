namespace OrbitalSentinel.Core;

public interface IConjunctionScanner
{
    ConjunctionScanResult Scan(PredictionRunContext run, IOrbitPropagator propagator, CancellationToken cancellationToken = default);
}

public static class ConjunctionAlgorithms
{
    public const string Current = "teme-relative-motion-brent-v1";
    public const string PropagatorVersion = "SGP.NET-1.6-WGS84";
}

/// <summary>Engineering screening parameters, not collision-risk or uncertainty thresholds.</summary>
public sealed record ConjunctionSettings(
    double CandidateDistanceMeters = 50_000,
    double ReportingDistanceMeters = 10_000,
    double TimeToleranceSeconds = 0.01,
    double AssociationWindowSeconds = 300,
    double RelativeAccelerationGuardMetersPerSecondSquared = 25,
    double RefinementSubdivisionSeconds = 15,
    int MaximumRefinementIterations = 128)
{
    public void Validate()
    {
        PositiveFinite(CandidateDistanceMeters, nameof(CandidateDistanceMeters));
        PositiveFinite(ReportingDistanceMeters, nameof(ReportingDistanceMeters));
        if (ReportingDistanceMeters > CandidateDistanceMeters || CandidateDistanceMeters > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(CandidateDistanceMeters),
                "Candidate distance must be at least the reporting distance and at most 1,000 km.");
        PositiveFinite(TimeToleranceSeconds, nameof(TimeToleranceSeconds));
        if (TimeToleranceSeconds is < 0.0001 or > 1)
            throw new ArgumentOutOfRangeException(nameof(TimeToleranceSeconds), "Time tolerance must be between 0.1 ms and one second.");
        PositiveFinite(AssociationWindowSeconds, nameof(AssociationWindowSeconds));
        if (AssociationWindowSeconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(AssociationWindowSeconds));
        if (!double.IsFinite(RelativeAccelerationGuardMetersPerSecondSquared) ||
            RelativeAccelerationGuardMetersPerSecondSquared is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(RelativeAccelerationGuardMetersPerSecondSquared));
        PositiveFinite(RefinementSubdivisionSeconds, nameof(RefinementSubdivisionSeconds));
        if (RefinementSubdivisionSeconds is < 1 or > 30 || RefinementSubdivisionSeconds < TimeToleranceSeconds * 4)
            throw new ArgumentOutOfRangeException(nameof(RefinementSubdivisionSeconds));
        if (MaximumRefinementIterations is < 16 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaximumRefinementIterations));
    }

    private static void PositiveFinite(double value, string parameter)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameter, "The setting must be finite and positive.");
    }
}

/// <summary>A TEME state in SI units, evaluated directly from pinned orbital elements at TCA.</summary>
public sealed record TemeStateSi(
    DateTimeOffset TimestampUtc,
    CartesianVector PositionMeters,
    CartesianVector VelocityMetersPerSecond);

/// <summary>A transient broad-phase estimate. It is never a final conjunction assessment.</summary>
public sealed record ConjunctionCandidate(
    Guid PrimaryObjectId,
    Guid SecondaryObjectId,
    DateTimeOffset IntervalStartUtc,
    DateTimeOffset IntervalEndUtc,
    DateTimeOffset EstimatedClosestApproachUtc,
    double EstimatedMissDistanceMeters);

/// <summary>
/// Numerical encounter result. Extended proximity has no unique interior TCA; the earliest
/// representative instant is reported. A boundary result is only minimal within the run window.
/// </summary>
public sealed record RefinedConjunction(
    Guid PrimaryObjectId,
    Guid SecondaryObjectId,
    DateTimeOffset TimeOfClosestApproachUtc,
    double MissDistanceMeters,
    double RelativeVelocityMetersPerSecond,
    TemeStateSi PrimaryState,
    TemeStateSi SecondaryState,
    bool IsWindowBoundary,
    bool IsExtendedProximity = false);

public sealed record ConjunctionScanMetrics(
    int ObjectCount,
    long PairCount,
    long IntervalCount,
    long CandidateCount,
    int RefinedCount,
    TimeSpan ScanDuration,
    TimeSpan RefinementDuration);

public sealed record ConjunctionScanResult(
    IReadOnlyList<RefinedConjunction> Encounters,
    ConjunctionScanMetrics Metrics);
