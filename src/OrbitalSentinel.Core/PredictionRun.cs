namespace OrbitalSentinel.Core;

/// <summary>
/// Immutable input manifest for one coherent prediction. Samples are a disposable calculation
/// cache, not part of the durable run identity. All objects use the same clock and pinned elements.
/// </summary>
public sealed record PredictionRunContext(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset PredictionStartUtc,
    DateTimeOffset PredictionEndUtc,
    int StepSeconds,
    string Source,
    Guid? ScenarioId,
    string AlgorithmVersion,
    string PropagatorVersion,
    ConjunctionSettings Settings,
    IReadOnlyList<OrbitSnapshot> Objects);

/// <summary>A versioned, isolated set of synthetic orbital elements and a reproducible clock.</summary>
public sealed record SimulationScenario(
    Guid Id,
    string Name,
    string Description,
    int Version,
    DateTimeOffset ClockUtc,
    IReadOnlyList<OrbitSnapshot> Objects);
