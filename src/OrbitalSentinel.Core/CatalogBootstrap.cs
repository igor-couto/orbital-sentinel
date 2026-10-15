namespace OrbitalSentinel.Core;

/// <summary>Only the identity and revision data needed to display the catalog and its markers.</summary>
public sealed record CatalogObjectSummary(
    Guid Id,
    long? NoradCatalogId,
    string Name,
    string? InternationalDesignator,
    string ObjectType);

public sealed record CatalogEntry(CatalogObjectSummary Object, long Version);

public sealed record CatalogBootstrap(IReadOnlyList<CatalogEntry> Items, long TotalCount);
