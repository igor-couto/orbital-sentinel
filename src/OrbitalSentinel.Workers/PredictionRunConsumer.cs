using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;

namespace OrbitalSentinel.Workers;

/// <summary>Kafka delivery ends at a durable job, not after a potentially long numerical scan.</summary>
public sealed class PredictionRunConsumer(ConjunctionRepository repository, ConsumerFailureRepository failures,
    IOptions<KafkaOptions> options, ILogger<PredictionRunConsumer> logger) : BackgroundService
{
    public async Task HandleAsync(string payload, CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<PredictionRunCompletedEvent>(payload, Serialization.Options)
            ?? throw new InvalidDataException("Missing prediction run event.");
        if (message.EventType != PredictionRunCompletedEvent.TypeName || message.SchemaVersion != 1
            || message.EventId == Guid.Empty || message.PredictionRunId == Guid.Empty)
            throw new InvalidDataException("Invalid prediction run event.");
        await repository.TryEnqueueScanAsync(message, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var consumer = KafkaTopics.CreateConsumer(options.Value, options.Value.ConjunctionGroup, AutoOffsetReset.Earliest);
            try
            {
                await KafkaTopics.EnsureAsync(options.Value, stoppingToken);
                consumer.Subscribe(options.Value.PredictionRunTopic);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var record = consumer.Consume(stoppingToken);
                    try { await HandleAsync(record.Message.Value, stoppingToken); }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                    {
                        await failures.QuarantineAsync(record.Topic, record.Partition.Value, record.Offset.Value,
                            record.Message.Key, record.Message.Value ?? "null", ex.Message, stoppingToken);
                        Telemetry.QuarantinedMessages.Add(1);
                        logger.LogError(ex, "Invalid prediction-run event quarantined at {Partition}/{Offset}", record.Partition.Value, record.Offset.Value);
                    }
                    consumer.Commit(record);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Prediction-run consumer interrupted; uncommitted records will be redelivered");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
            finally
            {
                try { consumer.Close(); }
                catch (KafkaException ex) { logger.LogWarning(ex, "Prediction-run consumer could not close"); }
            }
        }
    }
}
