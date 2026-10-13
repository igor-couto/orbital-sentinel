using OrbitalSentinel.Core;

namespace OrbitalSentinel.Propagation;

/// <summary>
/// Visualization-only TEME to pseudo-Earth-fixed rotation using Vallado's GMST expression.
/// UTC approximates UT1; polar motion and Earth orientation corrections are omitted.
/// Result is adequate for the demo globe but must not be labelled precise ITRF.
/// Velocity conversion is intentionally absent: rotating velocities also requires Earth rotation terms.
/// </summary>
public static class TemeToEarthFixed
{
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
        return new VisualizationPoint(state.TimestampUtc.ToUniversalTime(), new CartesianVector(
            1000 * (cos * position.X + sin * position.Y),
            1000 * (-sin * position.X + cos * position.Y),
            1000 * position.Z), OrbitDisplayGeometry.AltitudeKilometers(position), speed);
    }

    public static double GreenwichMeanSiderealTimeRadians(DateTimeOffset timestampUtc)
    {
        // JD at Unix epoch is exact; UTC ticks retain sub-second precision before conversion.
        var daysSinceJ2000 = (timestampUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) /
            (double)TimeSpan.TicksPerDay + 2440587.5 - 2451545.0;
        var centuries = daysSinceJ2000 / 36525.0;
        var seconds = 67310.54841 + (876600.0 * 3600 + 8640184.812866) * centuries
            + 0.093104 * centuries * centuries
            - 6.2e-6 * centuries * centuries * centuries;
        var radians = seconds * (Math.PI / 43200.0) % (2 * Math.PI);
        return radians < 0 ? radians + 2 * Math.PI : radians;
    }
}
