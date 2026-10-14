using System.Text.Json;
using Dapper;
using Npgsql;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.Infrastructure;

public sealed class CatalogRepository(NpgsqlDataSource dataSource, bool publishOrbitEvents = true)
{
    private const string SelectSnapshot = """
        SELECT o.id AS Id, o.norad_catalog_id AS NoradCatalogId, o.name AS Name,
               o.international_designator AS InternationalDesignator,
               CASE WHEN o.object_type IN ('Unknown','UNK') AND m.object_type IN ('PAY','R/B','DEB')
                    THEN m.object_type ELSE o.object_type END AS ObjectType,
               o.updated_at AS UpdatedAt, e.version AS Version, e.elements::text AS Elements,
               e.fingerprint AS Fingerprint, e.ingested_at AS IngestedAt
        FROM orbital_objects o JOIN orbital_element_sets e ON e.orbital_object_id = o.id
        LEFT JOIN LATERAL (
            SELECT payload->>'OBJECT_TYPE' AS object_type FROM catalog_metadata
            WHERE norad_catalog_id=o.norad_catalog_id ORDER BY retrieved_at DESC,group_name LIMIT 1
        ) m ON true
        """;

    public async Task<bool> UpsertAsync(CatalogImport import, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(import);
        import.Elements.Validate();
        if (string.IsNullOrWhiteSpace(import.Name) || import.Name.Length > 200)
            throw new ArgumentException("An object name of at most 200 characters is required.", nameof(import));
        if (import.NoradCatalogId is <= 0 || import.InternalId == Guid.Empty)
            throw new ArgumentException("Object identifiers must be positive/nonempty when supplied.", nameof(import));
        if (string.IsNullOrWhiteSpace(import.ObjectType) || import.ObjectType.Length > 100)
            throw new ArgumentException("An object type is required; use Unknown when the source does not supply it.", nameof(import));
        var id = import.InternalId ?? Guid.NewGuid();
        var key = import.NoradCatalogId is { } norad ? $"norad:{norad}" : $"object:{id:D}";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", new { key }, transaction,
            cancellationToken: cancellationToken));
        var existing = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(
            SelectSnapshot + """
             WHERE (o.id = @id OR (@norad IS NOT NULL AND o.norad_catalog_id = @norad))
                 AND e.version = o.current_version FOR UPDATE OF o;
            """, new { id, norad = import.NoradCatalogId }, transaction, cancellationToken: cancellationToken));
        if (existing is not null && import.InternalId is { } internalId && internalId != existing.Id)
            throw new ArgumentException("The internal ID does not match the existing NORAD identity.", nameof(import));
        if (existing is not null && existing.NoradCatalogId != import.NoradCatalogId)
            throw new ArgumentException("Changing an existing object's external identity is not supported.", nameof(import));
        if (existing is not null && import.Elements.EpochUtc < existing.ToSnapshot().Elements.EpochUtc)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var fingerprint = import.Elements.ComputeFingerprint();
        var changed = existing is null || existing.Fingerprint != fingerprint;
        id = existing?.Id ?? id;
        var version = existing is null ? 1 : existing.Version + (changed ? 1 : 0);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO orbital_objects(id,norad_catalog_id,name,international_designator,object_type,current_version,updated_at)
            VALUES (@id,@norad,@Name,@InternationalDesignator,@ObjectType,@version,now())
            ON CONFLICT (id) DO UPDATE SET name=excluded.name, international_designator=excluded.international_designator,
                object_type=excluded.object_type, current_version=excluded.current_version, updated_at=excluded.updated_at;
            """, new { id, norad = import.NoradCatalogId, import.Name, import.InternationalDesignator, import.ObjectType, version },
            transaction, cancellationToken: cancellationToken));
        if (changed)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO orbital_element_sets(orbital_object_id,version,epoch_utc,elements,fingerprint,ingested_at)
                VALUES (@id,@version,@epoch,CAST(@elements AS jsonb),@fingerprint,now());
                """, new { id, version, epoch = import.Elements.EpochUtc.ToUniversalTime(),
                    elements = JsonSerializer.Serialize(import.Elements, Serialization.Options), fingerprint }, transaction,
                cancellationToken: cancellationToken));
            if (publishOrbitEvents)
                await OutboxWriter.AppendAsync(connection, transaction, OrbitUpdatedEvent.Create(id, version), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<Page<OrbitSnapshot>> ListAsync(int page, int pageSize, string? search,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > 1_000_000 || pageSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be 1..1000000 and page size 1..200.");
        const string filter = " WHERE (@search IS NULL OR o.name ILIKE @pattern OR o.norad_catalog_id::text = @search)";
        var parameters = new { search, pattern = $"%{search}%", offset = (page - 1) * pageSize, pageSize };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM orbital_objects o" + filter, parameters, transaction, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<SnapshotRow>(new CommandDefinition(SelectSnapshot + filter +
            " AND e.version=o.current_version ORDER BY o.name,o.id LIMIT @pageSize OFFSET @offset;", parameters,
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new Page<OrbitSnapshot>(rows.Select(x => x.ToSnapshot()).ToArray(), page, pageSize, total);
    }

    public async Task<OrbitSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(
            SelectSnapshot + " WHERE o.id=@id AND e.version=o.current_version;", new { id }, cancellationToken: cancellationToken));
        return row?.ToSnapshot();
    }

    public async Task<IReadOnlyList<OrbitSnapshot>> ListAllAsync(CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<SnapshotRow>(new CommandDefinition(SelectSnapshot +
            " WHERE e.version=o.current_version ORDER BY o.name,o.id;", cancellationToken: ct));
        return rows.Select(x => x.ToSnapshot()).ToArray();
    }

    public async Task<OrbitSnapshot?> GetVersionAsync(Guid id, long version, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(
            SelectSnapshot + " WHERE o.id=@id AND e.version=@version;", new { id, version }, cancellationToken: cancellationToken));
        return row?.ToSnapshot();
    }

    public async Task<int> EnqueueRefreshAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken = default)
    {
        if (!publishOrbitEvents) return 0;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var objects = await connection.QueryAsync<RefreshRow>(new CommandDefinition("""
            SELECT o.id AS Id, o.current_version AS Version FROM orbital_objects o
            LEFT JOIN trajectory_predictions t ON t.orbital_object_id=o.id
            WHERE (t.orbital_object_id IS NULL OR t.end_utc < @beforeUtc OR t.orbit_version < o.current_version
                OR t.payload->'points'->0->>'speedKilometersPerSecond' IS NULL)
            ORDER BY o.id FOR UPDATE OF o;
            """, new { beforeUtc = beforeUtc.ToUniversalTime() }, transaction, cancellationToken: cancellationToken));
        var count = 0;
        foreach (var item in objects)
        {
            // Check after the object lock is acquired so concurrent refreshers see committed events.
            var pending = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE orbital_object_id=@Id
                    AND event_type IN (@eventType,@refreshType) AND published_at IS NULL AND dead_lettered_at IS NULL);
                """, new { item.Id, eventType = OrbitUpdatedEvent.TypeName,
                    refreshType = TrajectoryRefreshRequestedEvent.TypeName }, transaction, cancellationToken: cancellationToken));
            if (pending) continue;
            var message = new TrajectoryRefreshRequestedEvent(Guid.NewGuid(), TrajectoryRefreshRequestedEvent.TypeName,
                DateTimeOffset.UtcNow, Guid.NewGuid(), 1, item.Id, item.Version);
            await OutboxWriter.AppendAsync(connection, transaction, message.EventId, message.EventType, item.Id, item.Version,
                item.Id.ToString("D"), JsonSerializer.Serialize(message, Serialization.Options), message.OccurredAt, cancellationToken);
            count++;
        }
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    private sealed record RefreshRow(Guid Id, long Version);
    private sealed class SnapshotRow
    {
        public Guid Id { get; init; }
        public long? NoradCatalogId { get; init; }
        public string Name { get; init; } = "";
        public string? InternationalDesignator { get; init; }
        public string ObjectType { get; init; } = "";
        public DateTime UpdatedAt { get; init; }
        public long Version { get; init; }
        public string Elements { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public DateTime IngestedAt { get; init; }
        public OrbitSnapshot ToSnapshot() => new(new OrbitalObject(Id, NoradCatalogId, Name,
            InternationalDesignator, ObjectType, UpdatedAt), Version,
            JsonSerializer.Deserialize<OrbitalElements>(Elements, Serialization.Options)
                ?? throw new InvalidOperationException("An orbital element set is missing its payload."), Fingerprint, IngestedAt);
    }
}
