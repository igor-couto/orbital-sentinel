using System.Text.Json;
using Dapper;
using Npgsql;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.Infrastructure;

public sealed class TrajectoryRepository(NpgsqlDataSource dataSource)
{
    public async Task<Trajectory?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT t.payload::text FROM trajectory_predictions t JOIN orbital_objects o ON o.id=t.orbital_object_id
            WHERE t.orbital_object_id=@id AND t.orbit_version=o.current_version;
            """, new { id }, cancellationToken: cancellationToken));
        return payload is null ? null : JsonSerializer.Deserialize<Trajectory>(payload, Serialization.Options);
    }

    public async Task<bool> SaveAsync(Guid eventId, Trajectory trajectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        if (eventId == Guid.Empty || trajectory.OrbitalObjectId == Guid.Empty || trajectory.OrbitVersion < 1
            || trajectory.EndUtc < trajectory.StartUtc || trajectory.StepSeconds < 1 || trajectory.Points.Count < 1)
            throw new ArgumentException("A trajectory must have valid identity, bounds, step and samples.", nameof(trajectory));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentVersion = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT current_version FROM orbital_objects WHERE id=@id FOR UPDATE;", new { id = trajectory.OrbitalObjectId },
            transaction, cancellationToken: cancellationToken));
        var registered = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO processed_messages(consumer_name,event_id) VALUES ('trajectory-propagation',@eventId)
            ON CONFLICT DO NOTHING;
            """, new { eventId }, transaction, cancellationToken: cancellationToken));
        if (registered == 0 || trajectory.OrbitVersion < currentVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var applied = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO trajectory_predictions(orbital_object_id,orbit_version,start_utc,end_utc,payload)
            VALUES (@id,@version,@startUtc,@endUtc,CAST(@payload AS jsonb))
            ON CONFLICT (orbital_object_id) DO UPDATE SET orbit_version=excluded.orbit_version,
                start_utc=excluded.start_utc,end_utc=excluded.end_utc,payload=excluded.payload,updated_at=now()
            WHERE trajectory_predictions.orbit_version<excluded.orbit_version OR
                (trajectory_predictions.orbit_version=excluded.orbit_version AND trajectory_predictions.start_utc<excluded.start_utc);
            """, new { id = trajectory.OrbitalObjectId, version = trajectory.OrbitVersion,
                startUtc = trajectory.StartUtc.ToUniversalTime(), endUtc = trajectory.EndUtc.ToUniversalTime(),
                payload = JsonSerializer.Serialize(trajectory, Serialization.Options) }, transaction,
            cancellationToken: cancellationToken));
        if (applied == 1)
        {
            var message = new TrajectoryUpdatedEvent(Guid.NewGuid(), TrajectoryUpdatedEvent.TypeName,
                DateTimeOffset.UtcNow, eventId, 1, trajectory.OrbitalObjectId, trajectory.OrbitVersion,
                trajectory.StartUtc, trajectory.EndUtc);
            await OutboxWriter.AppendAsync(connection, transaction, message.EventId, message.EventType,
                message.OrbitalObjectId, message.OrbitVersion, message.OrbitalObjectId.ToString("D"),
                JsonSerializer.Serialize(message, Serialization.Options), message.OccurredAt, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return applied == 1;
    }

    public async Task<IReadOnlyList<ObjectPosition>> ListPositionsAsync(DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Extract only the nearest sample in SQL. Its actual timestamp is retained (no false precision).
        var rows = await connection.QueryAsync<PositionRow>(new CommandDefinition("""
            SELECT t.orbital_object_id AS OrbitalObjectId, sample.point::text AS Point
            FROM trajectory_predictions t JOIN orbital_objects o ON o.id=t.orbital_object_id
            CROSS JOIN LATERAL (
                SELECT point FROM jsonb_array_elements(t.payload->'points') AS points(point)
                ORDER BY abs(extract(epoch FROM (point->>'timestampUtc')::timestamptz - @atUtc)) LIMIT 1
            ) sample
            WHERE t.orbit_version=o.current_version AND t.start_utc<=@atUtc AND t.end_utc>=@atUtc;
            """, new { atUtc = atUtc.ToUniversalTime() }, cancellationToken: cancellationToken));
        return rows.Select(x =>
        {
            var point = JsonSerializer.Deserialize<VisualizationPoint>(x.Point, Serialization.Options)
                ?? throw new InvalidOperationException("Missing trajectory position payload.");
            return new ObjectPosition(x.OrbitalObjectId, point.TimestampUtc, point.PositionEcefMeters);
        }).ToArray();
    }

    private sealed record PositionRow(Guid OrbitalObjectId, string Point);
}
