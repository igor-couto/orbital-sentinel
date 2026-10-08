using System.ComponentModel.DataAnnotations;

namespace OrbitalSentinel.Runtime;

public sealed class KafkaOptions
{
    [Required] public string BootstrapServers { get; set; } = "localhost:19092";
    [Required] public string OrbitTopic { get; set; } = "orbital-sentinel.orbits.v1";
    [Required] public string TrajectoryTopic { get; set; } = "orbital-sentinel.trajectory-updated.v1";
    [Required] public string DeadLetterTopic { get; set; } = "orbital-sentinel.dead-letter.v1";
    [Required] public string PropagationGroup { get; set; } = "orbital-sentinel-propagation-v1";
    [Range(1, 32)] public int Partitions { get; set; } = 3;

    public string TopicFor(string eventType) => eventType switch
    {
        "orbit.updated.v1" => OrbitTopic,
        "trajectory.refresh-requested.v1" => OrbitTopic,
        "trajectory.updated.v1" => TrajectoryTopic,
        "consumer.dead-letter.v1" => DeadLetterTopic,
        _ => throw new ArgumentException("Unsupported outbox event type.", nameof(eventType))
    };
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
