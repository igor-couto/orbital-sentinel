using System.ComponentModel.DataAnnotations;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.Runtime;

public sealed class KafkaOptions
{
    [Required] public string BootstrapServers { get; set; } = "localhost:19092";
    [Required] public string OrbitTopic { get; set; } = "orbital-sentinel.orbits.v1";
    [Required] public string TrajectoryTopic { get; set; } = "orbital-sentinel.trajectory-updated.v1";
    [Required] public string DeadLetterTopic { get; set; } = "orbital-sentinel.dead-letter.v1";
    [Required] public string PropagationGroup { get; set; } = "orbital-sentinel-propagation-v1";
    [Required] public string PredictionRunTopic { get; set; } = "orbital-sentinel.prediction-runs.v1";
    [Required] public string ConjunctionTopic { get; set; } = "orbital-sentinel.conjunctions.v1";
    [Required] public string ConjunctionGroup { get; set; } = "orbital-sentinel-conjunctions-v1";
    [Range(1, 32)] public int Partitions { get; set; } = 3;

    public string TopicFor(string eventType) => eventType switch
    {
        "orbit.updated.v1" => OrbitTopic,
        "trajectory.refresh-requested.v1" => OrbitTopic,
        "trajectory.updated.v1" => TrajectoryTopic,
        "consumer.dead-letter.v1" => DeadLetterTopic,
        "prediction-run.completed.v1" => PredictionRunTopic,
        "conjunction.identified.v1" => ConjunctionTopic,
        "conjunction.assessment-created.v1" => ConjunctionTopic,
        _ => throw new ArgumentException("Unsupported outbox event type.", nameof(eventType))
    };
}

public sealed class ConjunctionOptions
{
    public bool Enabled { get; set; } = true;
    [Range(10, 720)] public int RunIntervalMinutes { get; set; } = 60;
    [Range(30, 300)] public int SamplingIntervalSeconds { get; set; } = 60;
    [Range(1, 200)] public double CandidateDistanceKm { get; set; } = 50;
    [Range(0.001, 100)] public double ReportingDistanceKm { get; set; } = 10;
    [Range(0.001, 1)] public double TimeToleranceSeconds { get; set; } = 0.01;
    [Range(1, 900)] public double AssociationWindowSeconds { get; set; } = 300;
    [Range(1, 100)] public double RelativeAccelerationGuardMetersPerSecondSquared { get; set; } = 25;
    [Range(1, 30)] public double RefinementSubdivisionSeconds { get; set; } = 15;
    [Range(1, 60)] public int MaximumScanMinutes { get; set; } = 15;

    public ConjunctionSettings ToSettings() => new(
        CandidateDistanceMeters: CandidateDistanceKm * 1000,
        ReportingDistanceMeters: ReportingDistanceKm * 1000,
        TimeToleranceSeconds: TimeToleranceSeconds,
        AssociationWindowSeconds: AssociationWindowSeconds,
        RelativeAccelerationGuardMetersPerSecondSquared: RelativeAccelerationGuardMetersPerSecondSquared,
        RefinementSubdivisionSeconds: RefinementSubdivisionSeconds);
}

public sealed class PredictionOptions
{
    [Range(1, 48)] public int HorizonHours { get; set; } = 24;
    [Range(30, 600)] public int StepSeconds { get; set; } = 60;
}

public sealed class TrafficOptions
{
    [Range(5, 120)] public int KeepAliveSeconds { get; set; } = 15;
    [Range(15, 300)] public int ClientTimeoutSeconds { get; set; } = 45;
}
