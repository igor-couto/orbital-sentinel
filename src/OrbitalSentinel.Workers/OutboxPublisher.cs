using Confluent.Kafka;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;

namespace OrbitalSentinel.Workers;

public sealed class OutboxPublisher(OutboxRepository outbox, IProducer<string, string> producer,
    IOptions<KafkaOptions> options, ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topicsReady = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!topicsReady)
                {
                    await KafkaTopics.EnsureAsync(options.Value, stoppingToken);
                    topicsReady = true;
                }
                // Claims contain at most one event per partition key. Start every publication
                // immediately so none of the leases waits behind a sequential batch.
                var messages = await outbox.ClaimAsync(16, TimeSpan.FromSeconds(60), stoppingToken);
                await Task.WhenAll(messages.Select(async message =>
                {
                    try
                    {
                        await producer.ProduceAsync(options.Value.TopicFor(message.EventType), new Message<string, string>
                        {
                            Key = message.PartitionKey,
                            Value = message.Payload
                        }, stoppingToken);
                        await outbox.MarkPublishedAsync(message.Id, message.LeaseToken, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Telemetry.PublicationFailures.Add(1);
                        logger.LogWarning(ex, "Outbox publication failed for {EventId}; attempt {RetryCount}", message.Id, message.RetryCount);
                        await outbox.MarkFailedAsync(message.Id, message.LeaseToken, ex.GetType().Name, stoppingToken);
                    }
                }));
                if (messages.Count == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publisher unavailable; durable messages remain in PostgreSQL");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
