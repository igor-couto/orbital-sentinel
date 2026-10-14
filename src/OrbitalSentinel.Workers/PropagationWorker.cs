using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Npgsql;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;

namespace OrbitalSentinel.Workers;

public sealed class PropagationWorker(CatalogRepository catalog, TrajectoryRepository trajectories,
    ConsumerFailureRepository failures, IOrbitPropagator propagator,
    IOptions<KafkaOptions> kafka, IOptions<PredictionOptions> prediction,
    ILogger<PropagationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var consumer = KafkaTopics.CreateConsumer(kafka.Value, kafka.Value.PropagationGroup, AutoOffsetReset.Earliest);
            try
            {
                await KafkaTopics.EnsureAsync(kafka.Value, stoppingToken);
                consumer.Subscribe(kafka.Value.OrbitTopic);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var record = consumer.Consume(stoppingToken);
                    using var scope = logger.BeginScope(new Dictionary<string, object>
                    {
                        ["KafkaTopic"] = record.Topic, ["KafkaPartition"] = record.Partition.Value, ["KafkaOffset"] = record.Offset.Value
                    });
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            await HandleAsync(record.Message.Value, stoppingToken);
                            break;
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or KafkaException)
                        {
                            // An infrastructure outage is not evidence of a poison message. Leave the offset uncommitted.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            var permanent = ex is JsonException or ArgumentException or InvalidDataException or OrbitPropagationException;
                            if (!permanent && attempt < 3)
                            {
                                logger.LogWarning(ex, "Unexpected propagation failure; retry {Attempt} of 3", attempt);
                                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), stoppingToken);
                                continue;
                            }
                            await failures.QuarantineAsync(record.Topic, record.Partition.Value, record.Offset.Value,
                                record.Message.Key, record.Message.Value ?? "null", ex.Message, stoppingToken);
                            Telemetry.QuarantinedMessages.Add(1);
                            logger.LogError(ex, "Propagation failure quarantined after {Attempt} attempt(s)", attempt);
                            break;
                        }
                    }
                    // Commit only after the cache + inbox + outgoing event (or quarantine) commit in PostgreSQL.
                    consumer.Commit(record);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Propagation consumer interrupted; uncommitted messages will be redelivered");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
            finally
            {
                try { consumer.Close(); }
                catch (KafkaException ex) { logger.LogWarning(ex, "Consumer group close failed during recovery"); }
            }
        }
    }

    public async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<OrbitUpdatedEvent>(payload, Serialization.Options)
            ?? throw new InvalidDataException("Empty OrbitUpdated event.");
        if (message.EventType is not ("orbit.updated.v1" or "trajectory.refresh-requested.v1") || message.SchemaVersion != 1 || message.EventId == Guid.Empty
            || message.OrbitalObjectId == Guid.Empty || message.OrbitVersion <= 0)
            throw new InvalidDataException("Unsupported or invalid OrbitUpdated event.");
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["EventId"] = message.EventId, ["CorrelationId"] = message.CorrelationId, ["OrbitalObjectId"] = message.OrbitalObjectId
        });
        using var activity = Telemetry.Activities.StartActivity("orbit.propagate");
        var orbit = await catalog.GetVersionAsync(message.OrbitalObjectId, message.OrbitVersion, cancellationToken)
            ?? throw new InvalidDataException("Referenced orbital element version does not exist.");
        if (!prediction.Value.PrecomputeTrajectories)
        {
            // The API computes shared current positions and requested paths from the catalog.
            // Keep consumption idempotent without writing millions of unused trajectory samples.
            await trajectories.AcknowledgeAsync(message.EventId, cancellationToken);
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        // Truncate to a UTC minute. Deterministic sampling within a delivery attempt and a bounded rolling cache.
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        var trajectory = propagator.Predict(orbit, start, TimeSpan.FromHours(prediction.Value.HorizonHours),
            TimeSpan.FromSeconds(prediction.Value.StepSeconds), cancellationToken);
        var applied = await trajectories.SaveAsync(message.EventId, trajectory, cancellationToken);
        if (applied) Telemetry.Propagations.Add(1);
        Telemetry.PropagationDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        logger.LogInformation("Propagation processed: version {OrbitVersion}, applied {Applied}, duration {DurationMs} ms", message.OrbitVersion, applied, stopwatch.Elapsed.TotalMilliseconds);
    }
}
