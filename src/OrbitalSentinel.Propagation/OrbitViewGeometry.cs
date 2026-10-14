using OrbitalSentinel.Core;

namespace OrbitalSentinel.Propagation;

/// <summary>Prepare an orbit-shape overlay without re-propagating or changing the Earth-relative trajectory.</summary>
public static class OrbitViewGeometry
{
    public static Trajectory AddTo(Trajectory trajectory)
    {
        var reference = TemeToEarthFixed.UnwrappedGmstRadians(trajectory.StartUtc);
        var points = trajectory.Points.Select(point =>
        {
            var delta = TemeToEarthFixed.UnwrappedGmstRadians(point.TimestampUtc) - reference;
            var (sin, cos) = Math.SinCos(delta);
            CartesianVector Rotate(CartesianVector v) => new(cos * v.X - sin * v.Y,
                sin * v.X + cos * v.Y, v.Z);
            var p = point.PositionEcefMeters;
            // Undo the rotating-frame derivative before putting velocity in the fixed reference axes.
            var v = point.VelocityEcefMetersPerSecond;
            var referenceVelocity = v is null ? null : Rotate(new CartesianVector(
                v.X - TemeToEarthFixed.EarthRotationRadiansPerSecond * p.Y,
                v.Y + TemeToEarthFixed.EarthRotationRadiansPerSecond * p.X, v.Z));
            return point with { OrbitView = new OrbitViewPoint(Rotate(p), referenceVelocity, -delta) };
        }).ToArray();
        return trajectory with { Points = points };
    }
}
