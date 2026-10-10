namespace OrbitalSentinel.Core;

/// <summary>
/// Synthetic near-circular orbits in opposite-inclination planes. Both approach the common
/// line of nodes shortly after the fixed simulation clock. These are never catalog records.
/// Version the scenario when changing any element, epoch, or clock.
/// </summary>
public static class CrossingOrbitsSimulation
{
    public static readonly Guid ScenarioId = Guid.Parse("c7919ca3-4bfd-4d7b-8574-c9cd199f614d");
    public const int Version = 1;

    public static SimulationScenario Create()
    {
        var epoch = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var first = new OrbitalElements(epoch, 15, 0.0001, 45, 0, 0, 0, 0, 0, 0);
        var second = first with { InclinationDegrees = 135 };
        return new SimulationScenario(ScenarioId, "Crossing orbits",
            "Two synthetic objects in crossing orbital planes. This reproducible scenario uses a fixed simulation clock and the same SGP4 screening as real data.",
            Version, epoch.AddMinutes(-5),
            [Snapshot(Guid.Parse("513f8d53-6aeb-4c57-b05c-a49150929171"), "SIMULATION · Crossing A", first),
             Snapshot(Guid.Parse("80cf98d0-bf72-4b4c-b10b-b17bc4ad3c80"), "SIMULATION · Crossing B", second)]);
    }

    private static OrbitSnapshot Snapshot(Guid id, string name, OrbitalElements elements) => new(
        new OrbitalObject(id, null, name, null, "SIMULATION", elements.EpochUtc),
        Version, elements, elements.ComputeFingerprint(), elements.EpochUtc);
}
