using System.Text.Json;
using Dapper;
using Npgsql;

namespace OrbitalSentinel.Infrastructure;

public sealed class ConsumerFailureRepository(NpgsqlDataSource dataSource)
{
    public async Task QuarantineAsync(string topic, int partition, long offset, string? key, string payload,
        string error, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic) || partition < 0 || offset < 0)
            throw new ArgumentException("A valid Kafka source position is required.");
        var id = Guid.NewGuid();
        var errorSummary = error.Length > 2000 ? error[..2000] : error;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO consumer_failures(id,topic,partition,offset_position,message_key,payload,error)
            VALUES (@id,@topic,@partition,@offset,@key,@payload,@errorSummary) ON CONFLICT DO NOTHING;
            """, new { id, topic, partition, offset, key, payload, errorSummary }, transaction, cancellationToken: cancellationToken));
        if (inserted == 1)
        {
            var occurredAt = DateTimeOffset.UtcNow;
            var message = new ConsumerDeadLetterEvent(id, ConsumerDeadLetterEvent.TypeName, occurredAt, id,
                1, topic, partition, offset, key, payload, errorSummary);
            await OutboxWriter.AppendAsync(connection, transaction, id, message.EventType, null, null,
                $"{topic}:{partition}", JsonSerializer.Serialize(message, Serialization.Options), occurredAt, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed record ConsumerDeadLetterEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt,
    Guid CorrelationId, int SchemaVersion, string SourceTopic, int SourcePartition, long SourceOffset,
    string? SourceKey, string SourcePayload, string Error)
{
    public const string TypeName = "consumer.dead-letter.v1";
}
