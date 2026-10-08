using OrbitalSentinel.Core;

namespace OrbitalSentinel.UnitTests;

internal static class ReferenceOrbit
{
    // Vanguard 1, catalog 5, Vallado AIAA 2006-6753 SGP4-VER.TLE verification case.
    // OMM-style numeric inputs preserve that case without a TLE parser in the application.
    public static OrbitalElements Elements => new(
        new DateTimeOffset(2000, 6, 27, 18, 50, 19, TimeSpan.Zero).AddTicks(7335680),
        10.82419157, 0.1859667, 34.2682, 348.7242, 331.7664, 19.3264,
        0.000028098, 0.00000023, 0);

    public static OrbitSnapshot Snapshot(OrbitalElements? elements = null)
    {
        elements ??= Elements;
        return new OrbitSnapshot(
            new OrbitalObject(Guid.Parse("27cb7166-c49d-4072-a6ba-8d5a53ebff93"), null,
                "VANGUARD 1", "1958-002B", "PAYLOAD", elements.EpochUtc),
            1, elements, elements.ComputeFingerprint(), elements.EpochUtc);
    }
}
