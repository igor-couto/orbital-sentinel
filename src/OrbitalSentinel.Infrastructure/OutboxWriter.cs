using System.Text.Json;
using Dapper;
using Npgsql;

namespace OrbitalSentinel.Infrastructure;

internal static class OutboxWriter
{
    public static Task AppendAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        OrbitUpdatedEvent message, CancellationToken cancellationToken) => AppendAsync(connection, transaction,
        message.EventId, message.EventType, message.OrbitalObjectId, message.OrbitVersion,
        message.OrbitalObjectId.ToString("D"), JsonSerializer.Serialize(message, Serialization.Options),
        message.OccurredAt, cancellationToken);

    public static Task AppendAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, string eventType, Guid? objectId, long? version, string partitionKey, string payload,
        DateTimeOffset occurredAt, CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO outbox_messages(id,event_type,orbital_object_id,orbit_version,partition_key,payload,occurred_at)
            VALUES (@id,@eventType,@objectId,@version,@partitionKey,CAST(@payload AS jsonb),@occurredAt);
            """, new { id, eventType, objectId, version, partitionKey, payload, occurredAt }, transaction,
            cancellationToken: cancellationToken));
}
