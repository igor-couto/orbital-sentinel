using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;

namespace OrbitalSentinel.Api;

public sealed class TrafficHub : Hub;

public sealed record TrajectoryAvailable(Guid OrbitalObjectId, long OrbitVersion, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>Each API replica broadcasts advisory notifications to its own connected browsers.</summary>
public sealed class TrafficNotifications(IHubContext<TrafficHub> hub, IOptions<KafkaOptions> options,
    IOptions<PredictionOptions> prediction, ILogger<TrafficNotifications> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A shared group would deliver a notification to only one API replica. Unique ephemeral groups fan out.
        var groupId = $"orbital-sentinel-browser-{Guid.NewGuid():N}";
        while (!stoppingToken.IsCancellationRequested)
        {
            using var consumer = KafkaTopics.CreateConsumer(options.Value, groupId, AutoOffsetReset.Latest);
            try
            {
                consumer.Subscribe(prediction.Value.PrecomputeTrajectories
                    ? new[] { options.Value.TrajectoryTopic, options.Value.ConjunctionTopic }
                    : new[] { options.Value.ConjunctionTopic });
                while (!stoppingToken.IsCancellationRequested)
                {
                    var record = consumer.Consume(stoppingToken);
                    try
                    {
                        if (record.Topic == options.Value.TrajectoryTopic)
                        {
                            var message = JsonSerializer.Deserialize<TrajectoryUpdatedEvent>(record.Message.Value, Serialization.Options);
                            if (message is { EventType: "trajectory.updated.v1", SchemaVersion: 1 })
                                await hub.Clients.All.SendAsync("TrajectoryAvailable",
                                    new TrajectoryAvailable(message.OrbitalObjectId, message.OrbitVersion, message.StartUtc, message.EndUtc), stoppingToken);
                        }
                        else
                        {
                            using var payload = JsonDocument.Parse(record.Message.Value);
                            if (payload.RootElement.TryGetProperty("eventType", out var type) &&
                                type.GetString() == ConjunctionAssessmentCreatedEvent.TypeName)
                            {
                                var message = JsonSerializer.Deserialize<ConjunctionAssessmentCreatedEvent>(record.Message.Value, Serialization.Options);
                                if (message is { SchemaVersion: 1 })
                                    await hub.Clients.All.SendAsync("ConjunctionAssessmentAvailable",
                                        new { message.ConjunctionId, message.AssessmentId, message.PredictionRunId, message.Source }, stoppingToken);
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning(ex, "Ignored malformed client-notification source at {KafkaTopic}/{KafkaPartition}/{KafkaOffset}", record.Topic, record.Partition.Value, record.Offset.Value);
                    }
                    consumer.Commit(record);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Live notification relay interrupted; browsers reconcile through REST polling");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
            finally
            {
                try { consumer.Close(); }
                catch (KafkaException ex) { logger.LogWarning(ex, "Notification consumer close failed"); }
            }
        }
    }
}
