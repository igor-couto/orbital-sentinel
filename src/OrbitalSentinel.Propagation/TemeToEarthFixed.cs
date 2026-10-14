using OrbitalSentinel.Core;

namespace OrbitalSentinel.Propagation;

/// <summary>
/// Visualization-only TEME to pseudo-Earth-fixed rotation using Vallado's GMST expression.
/// UTC approximates UT1; polar motion and Earth orientation corrections are omitted.
/// Result is adequate for the demo globe but must not be labelled precise ITRF.
/// Velocity includes the Earth-rotation term and is the derivative of the displayed position.
/// </summary>
public static class TemeToEarthFixed
{
    public const double EarthRotationRadiansPerSecond = 7.29211514670698e-5;
    public static VisualizationPoint Convert(StateVector state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var theta = GreenwichMeanSiderealTimeRadians(state.TimestampUtc);
        var (sin, cos) = Math.SinCos(theta);
        var position = state.PositionKm;

        var velocity = state.VelocityKmPerSecond;
        // Speed remains the magnitude of the SGP4 TEME velocity, not Earth-relative ground speed.
        var speed = Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y + velocity.Z * velocity.Z);
        // Geodetic height is invariant under the rotation about Earth's polar axis.
        var fixedPosition = new CartesianVector(
            1000 * (cos * position.X + sin * position.Y),
            1000 * (-sin * position.X + cos * position.Y),
            1000 * position.Z);
        // v_fixed = R v_TEME - omega × r_fixed, not merely R v_TEME.
        var fixedVelocity = new CartesianVector(
            1000 * (cos * velocity.X + sin * velocity.Y) + EarthRotationRadiansPerSecond * fixedPosition.Y,
            1000 * (-sin * velocity.X + cos * velocity.Y) - EarthRotationRadiansPerSecond * fixedPosition.X,
            1000 * velocity.Z);
        return new VisualizationPoint(state.TimestampUtc.ToUniversalTime(), fixedPosition,
            OrbitDisplayGeometry.AltitudeKilometers(position), speed, fixedVelocity);
    }

    public static double GreenwichMeanSiderealTimeRadians(DateTimeOffset timestampUtc)
    {
        var radians = UnwrappedGmstRadians(timestampUtc) % (2 * Math.PI);
        return radians < 0 ? radians + 2 * Math.PI : radians;
    }

    // Keeping complete turns lets display orientations interpolate across midnight and 2π.
    internal static double UnwrappedGmstRadians(DateTimeOffset timestampUtc)
    {
        // JD at Unix epoch is exact; UTC ticks retain sub-second precision before conversion.
        var daysSinceJ2000 = (timestampUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) /
            (double)TimeSpan.TicksPerDay + 2440587.5 - 2451545.0;
        var centuries = daysSinceJ2000 / 36525.0;
        var seconds = 67310.54841 + (876600.0 * 3600 + 8640184.812866) * centuries
            + 0.093104 * centuries * centuries
            - 6.2e-6 * centuries * centuries * centuries;
        return seconds * (Math.PI / 43200.0);
    }
}
