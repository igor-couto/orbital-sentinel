using OrbitalSentinel.Core;

namespace OrbitalSentinel.Infrastructure;

/// <summary>A sealed input manifest. Completed describes snapshot preparation, not conjunction screening.</summary>
public sealed record PredictionRunCompletedEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt,
    Guid CorrelationId, int SchemaVersion, Guid PredictionRunId, string Source, Guid? ScenarioId)
{
    public const string TypeName = "prediction-run.completed.v1";
}

public sealed record ConjunctionIdentifiedEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt,
    Guid CorrelationId, int SchemaVersion, Guid ConjunctionId, Guid PredictionRunId,
    Guid PrimaryObjectId, Guid SecondaryObjectId, string Source, Guid? ScenarioId)
{
    public const string TypeName = "conjunction.identified.v1";
}

public sealed record ConjunctionAssessmentCreatedEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt,
    Guid CorrelationId, int SchemaVersion, Guid ConjunctionId, Guid AssessmentId, Guid PredictionRunId,
    Guid PrimaryObjectId, Guid SecondaryObjectId, string Source, Guid? ScenarioId,
    DateTimeOffset TimeOfClosestApproachUtc, double MissDistanceMeters, double RelativeVelocityMetersPerSecond)
{
    public const string TypeName = "conjunction.assessment-created.v1";
}

public sealed record PredictionRunRecord(PredictionRunContext Context, string Status, int Attempts,
    string? LastError, DateTimeOffset? CompletedAtUtc, ConjunctionScanMetrics? Metrics)
{
    public Guid Id => Context.Id;
}

public sealed record ConjunctionScanLease(PredictionRunRecord Run, Guid LeaseToken);

public sealed record ConjunctionAssessmentRecord(Guid Id, Guid ConjunctionId, Guid PredictionRunId,
    DateTimeOffset DetectedAtUtc, DateTimeOffset TimeOfClosestApproachUtc, double MissDistanceMeters,
    double RelativeVelocityMetersPerSecond, TemeStateSi PrimaryState, TemeStateSi SecondaryState,
    long PrimaryOrbitVersion, long SecondaryOrbitVersion, bool IsWindowBoundary, bool IsExtendedProximity);

public sealed record ConjunctionSummary(Guid Id, string Source, Guid? ScenarioId, Guid PrimaryObjectId,
    Guid SecondaryObjectId, string PrimaryName, string SecondaryName, long? PrimaryNoradId,
    long? SecondaryNoradId, DateTimeOffset FirstIdentifiedAtUtc, ConjunctionAssessmentRecord LatestAssessment);

public sealed record ConjunctionQuery(string? Source = "Real", Guid? ObjectId = null, Guid? PredictionRunId = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, double? MaxMissDistanceMeters = null,
    int Page = 1, int PageSize = 20, bool LatestRunOnly = true);
