using System.Text.Json;
using System.Text.Json.Serialization;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.Infrastructure;

public sealed record CatalogImport(Guid? InternalId, long? NoradCatalogId, string Name,
    string? InternationalDesignator, string ObjectType, OrbitalElements Elements);

public sealed record Page<T>(IReadOnlyList<T> Items, [property: JsonPropertyName("page")] int PageNumber,
    int PageSize, long TotalCount);

public sealed record OrbitUpdatedEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt,
    Guid CorrelationId, int SchemaVersion, Guid OrbitalObjectId, long OrbitVersion)
{
    public const string TypeName = "orbit.updated.v1";
    public static OrbitUpdatedEvent Create(Guid objectId, long version) =>
        new(Guid.NewGuid(), TypeName, DateTimeOffset.UtcNow, Guid.NewGuid(), 1, objectId, version);
}

public sealed record OutboxLease(Guid Id, string EventType, string Payload, string PartitionKey,
    int RetryCount, Guid LeaseToken);

public sealed record TrajectoryUpdatedEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt,
    Guid CorrelationId, int SchemaVersion, Guid OrbitalObjectId, long OrbitVersion,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public const string TypeName = "trajectory.updated.v1";
}

public sealed record ObjectPosition(Guid OrbitalObjectId, DateTimeOffset TimestampUtc,
    CartesianVector PositionEcefMeters);

public sealed record TrajectoryRefreshRequestedEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt,
    Guid CorrelationId, int SchemaVersion, Guid OrbitalObjectId, long OrbitVersion)
{
    public const string TypeName = "trajectory.refresh-requested.v1";
}

public static class Serialization
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
