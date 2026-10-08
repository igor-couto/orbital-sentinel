namespace OrbitalSentinel.Core;

/// <summary>Internal identity is independent of optional external catalog identifiers.</summary>
public sealed record OrbitalObject(
    Guid Id,
    long? NoradCatalogId,
    string Name,
    string? InternationalDesignator,
    string ObjectType,
    DateTimeOffset UpdatedAt);

/// <summary>An immutable revision of the orbital elements for an internal object.</summary>
public sealed record OrbitSnapshot(
    OrbitalObject Object,
    long Version,
    OrbitalElements Elements,
    string Fingerprint,
    DateTimeOffset IngestedAt);
