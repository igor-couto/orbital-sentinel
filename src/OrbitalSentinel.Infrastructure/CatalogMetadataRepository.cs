using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;

namespace OrbitalSentinel.Infrastructure;

public sealed record SatelliteCatalogRecord(
    [property: JsonPropertyName("NORAD_CAT_ID")] long NoradCatalogId,
    [property: JsonPropertyName("OBJECT_TYPE")] string? ObjectType,
    [property: JsonPropertyName("OWNER")] string? OwnerCode,
    [property: JsonPropertyName("LAUNCH_DATE")] string? LaunchDate,
    [property: JsonPropertyName("LAUNCH_SITE")] string? LaunchSiteCode,
    [property: JsonPropertyName("OPS_STATUS_CODE")] string? OperationalStatusCode);

public sealed record CatalogMetadata(string? ObjectType, string? OwnerCode, string? LaunchDate,
    string? LaunchSiteCode, string? OperationalStatusCode, string[] Groups, DateTimeOffset RetrievedAtUtc);

public sealed class CatalogMetadataRepository(NpgsqlDataSource dataSource)
{
    public async Task SaveGroupAsync(string group, IReadOnlyList<SatelliteCatalogRecord> records,
        DateTimeOffset retrievedAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(group) || records.Count == 0)
            throw new ArgumentException("Only successful, nonempty catalog downloads can replace cached metadata.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM catalog_metadata WHERE group_name=@group;",
            new { group }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO catalog_metadata(norad_catalog_id,group_name,payload,retrieved_at)
            SELECT (record->>'NORAD_CAT_ID')::bigint,@group,record,@retrievedAt
            FROM jsonb_array_elements(CAST(@records AS jsonb)) record;
            """, new { records = JsonSerializer.Serialize(records), group, retrievedAt },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CatalogMetadata?> GetAsync(long noradCatalogId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition("""
            SELECT group_name AS GroupName, payload::text AS Payload, retrieved_at AS RetrievedAt
            FROM catalog_metadata WHERE norad_catalog_id=@noradCatalogId ORDER BY retrieved_at DESC,group_name;
            """, new { noradCatalogId }, cancellationToken: cancellationToken))).ToArray();
        if (rows.Length == 0) return null;
        var latest = JsonSerializer.Deserialize<SatelliteCatalogRecord>(rows[0].Payload)!;
        return new CatalogMetadata(latest.ObjectType, latest.OwnerCode, latest.LaunchDate, latest.LaunchSiteCode,
            latest.OperationalStatusCode, rows.Select(x => x.GroupName).Order(StringComparer.Ordinal).ToArray(),
            new DateTimeOffset(DateTime.SpecifyKind(rows[0].RetrievedAt, DateTimeKind.Utc)));
    }

    private sealed record Row(string GroupName, string Payload, DateTime RetrievedAt);
}
