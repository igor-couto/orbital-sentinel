namespace OrbitalSentinel.Core;

/// <summary>Approximate Keplerian apsides from the mean elements, not a propagated extremum search.</summary>
public sealed record MeanOrbitShape(double PeriodMinutes, double PerigeeKilometers, double ApogeeKilometers);

public static class OrbitDisplayGeometry
{
    // WGS84, matching the SGP4 adapter. Apsides use altitude above the equatorial radius.
    private const double EquatorialRadiusKm = 6378.137;
    private const double Flattening = 1 / 298.257223563;

    public static MeanOrbitShape MeanShape(OrbitalElements elements)
    {
        elements.Validate();
        var radiansPerSecond = elements.MeanMotionRevolutionsPerDay * 2 * Math.PI / 86400;
        var semiMajorAxisKm = Math.Cbrt(398600.5 / (radiansPerSecond * radiansPerSecond));
        return new MeanOrbitShape(1440 / elements.MeanMotionRevolutionsPerDay,
            semiMajorAxisKm * (1 - elements.Eccentricity) - EquatorialRadiusKm,
            semiMajorAxisKm * (1 + elements.Eccentricity) - EquatorialRadiusKm);
    }

    /// <summary>Geodetic height above the WGS84 ellipsoid; input is Earth-fixed kilometres.</summary>
    public static double AltitudeKilometers(CartesianVector position)
    {
        var p = Math.Sqrt(position.X * position.X + position.Y * position.Y);
        var polarRadiusKm = EquatorialRadiusKm * (1 - Flattening);
        if (p < 1e-9) return Math.Abs(position.Z) - polarRadiusKm;
        var eccentricitySquared = Flattening * (2 - Flattening);
        var secondEccentricitySquared = eccentricitySquared / (1 - eccentricitySquared);
        var theta = Math.Atan2(position.Z * EquatorialRadiusKm, p * polarRadiusKm);
        var (sinTheta, cosTheta) = Math.SinCos(theta);
        var latitude = Math.Atan2(position.Z + secondEccentricitySquared * polarRadiusKm * Math.Pow(sinTheta, 3),
            p - eccentricitySquared * EquatorialRadiusKm * Math.Pow(cosTheta, 3));
        var sinLatitude = Math.Sin(latitude);
        var radius = EquatorialRadiusKm / Math.Sqrt(1 - eccentricitySquared * sinLatitude * sinLatitude);
        return p / Math.Cos(latitude) - radius;
    }
}
