using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;

namespace OrbitalSentinel.Runtime;

public static class KafkaTopics
{
    public static async Task EnsureAsync(KafkaOptions options, bool precomputeTrajectories, CancellationToken cancellationToken)
    {
        string[] topics = precomputeTrajectories
            ? [options.OrbitTopic, options.TrajectoryTopic, options.DeadLetterTopic, options.PredictionRunTopic, options.ConjunctionTopic]
            : [options.DeadLetterTopic, options.PredictionRunTopic, options.ConjunctionTopic];
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(topics
                .Select(name => new TopicSpecification
                {
                    Name = name,
                    NumPartitions = options.Partitions,
                    ReplicationFactor = 1,
                    Configs = new Dictionary<string, string> { ["retention.ms"] = "604800000" }
                }), new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }).WaitAsync(cancellationToken);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Concurrent worker starts are safe. Production topics should be provisioned outside the app.
        }
    }

    public static IConsumer<string, string> CreateConsumer(KafkaOptions options, string groupId, AutoOffsetReset offsetReset) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = groupId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = offsetReset,
            MaxPollIntervalMs = 300000,
            SessionTimeoutMs = 10000,
            AllowAutoCreateTopics = false
        }).Build();
}
