using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OrbitalSentinel.Core;

/// <summary>
/// Earth-centered SGP4 mean elements, using UTC epochs and degrees for angles.
/// The mean-motion derivatives retain the source OMM values; SGP4 uses BStar for drag.
/// These are mean elements, not instantaneous osculating state vectors.
/// </summary>
public sealed record OrbitalElements(
    DateTimeOffset EpochUtc,
    double MeanMotionRevolutionsPerDay,
    double Eccentricity,
    double InclinationDegrees,
    double RightAscensionDegrees,
    double ArgumentOfPericenterDegrees,
    double MeanAnomalyDegrees,
    double BStar,
    double MeanMotionDot,
    double MeanMotionDdot)
{
    /// <summary>Validate at ingestion and before propagation, including after deserialization.</summary>
    public void Validate()
    {
        if (EpochUtc == default)
            throw new ArgumentException("An orbital epoch is required.", nameof(EpochUtc));

        RequireFinite(MeanMotionRevolutionsPerDay, nameof(MeanMotionRevolutionsPerDay));
        if (MeanMotionRevolutionsPerDay <= 0)
            throw new ArgumentOutOfRangeException(nameof(MeanMotionRevolutionsPerDay), "Mean motion must be positive.");

        RequireRange(Eccentricity, 0, 1, false, nameof(Eccentricity));
        RequireRange(InclinationDegrees, 0, 180, true, nameof(InclinationDegrees));
        RequireRange(RightAscensionDegrees, 0, 360, false, nameof(RightAscensionDegrees));
        RequireRange(ArgumentOfPericenterDegrees, 0, 360, false, nameof(ArgumentOfPericenterDegrees));
        RequireRange(MeanAnomalyDegrees, 0, 360, false, nameof(MeanAnomalyDegrees));
        RequireFinite(BStar, nameof(BStar));
        RequireFinite(MeanMotionDot, nameof(MeanMotionDot));
        RequireFinite(MeanMotionDdot, nameof(MeanMotionDdot));
    }

    /// <summary>
    /// A versioned, culture-independent content hash of every element, including epoch.
    /// Equivalent UTC instants and signed zero have identical fingerprints.
    /// Object names, external IDs and ingestion time deliberately do not participate.
    /// </summary>
    public string ComputeFingerprint()
    {
        Validate();
        var canonical = string.Join('|',
            "orbital-elements-v1",
            EpochUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Format(MeanMotionRevolutionsPerDay), Format(Eccentricity), Format(InclinationDegrees),
            Format(RightAscensionDegrees), Format(ArgumentOfPericenterDegrees), Format(MeanAnomalyDegrees),
            Format(BStar), Format(MeanMotionDot), Format(MeanMotionDdot));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Format(double value) => (value == 0 ? 0d : value).ToString("R", CultureInfo.InvariantCulture);

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Orbital elements must be finite numbers.");
    }

    private static void RequireRange(double value, double minimum, double maximum, bool inclusiveMaximum, string name)
    {
        RequireFinite(value, name);
        if (value < minimum || (inclusiveMaximum ? value > maximum : value >= maximum))
            throw new ArgumentOutOfRangeException(name, "The orbital element is outside its permitted range.");
    }
}
