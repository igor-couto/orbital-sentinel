using OrbitalSentinel.Core;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.UnitTests;

public sealed class OrbitDisplayGeometryTests
{
    [Theory]
    [InlineData(0, 400)]
    [InlineData(45, 400)]
    [InlineData(-70, 1200)]
    [InlineData(90, 500)]
    [InlineData(-90, 500)]
    public void Altitude_is_height_above_WGS84_ellipsoid(double latitudeDegrees, double altitude)
    {
        var latitude = latitudeDegrees * Math.PI / 180;
        const double eccentricitySquared = 0.0066943799901413165;
        var radius = 6378.137 / Math.Sqrt(1 - eccentricitySquared * Math.Pow(Math.Sin(latitude), 2));
        var position = new CartesianVector((radius + altitude) * Math.Cos(latitude), 0,
            (radius * (1 - eccentricitySquared) + altitude) * Math.Sin(latitude));
        Assert.InRange(Math.Abs(OrbitDisplayGeometry.AltitudeKilometers(position) - altitude), 0, 0.00001);
    }

    [Fact]
    public void Speed_uses_SGP4_TEME_velocity_and_survives_visualization_conversion()
    {
        var propagator = new Sgp4OrbitPropagator();
        var orbit = ReferenceOrbit.Snapshot();
        var state = propagator.Propagate(orbit, orbit.Elements.EpochUtc);
        var point = propagator.Predict(orbit, orbit.Elements.EpochUtc, TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1), CancellationToken.None).Points[0];
        var velocity = state.VelocityKmPerSecond;
        Assert.Equal(Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y + velocity.Z * velocity.Z),
            point.SpeedKilometersPerSecond);
        Assert.InRange(point.SpeedKilometersPerSecond!.Value, 7, 9);
        Assert.InRange(point.AltitudeKilometers!.Value, 700, 850);
    }

    [Fact]
    public void Circular_mean_orbit_has_equal_apsides_and_eccentricity_separates_them()
    {
        var elements = ReferenceOrbit.Snapshot().Elements with { MeanMotionRevolutionsPerDay = 15, Eccentricity = 0 };
        var circular = OrbitDisplayGeometry.MeanShape(elements);
        Assert.Equal(96, circular.PeriodMinutes);
        Assert.Equal(circular.PerigeeKilometers, circular.ApogeeKilometers);
        Assert.InRange(circular.PerigeeKilometers, 566, 568);
        var eccentric = OrbitDisplayGeometry.MeanShape(elements with { Eccentricity = 0.05 });
        Assert.True(eccentric.PerigeeKilometers < circular.PerigeeKilometers);
        Assert.True(eccentric.ApogeeKilometers > circular.ApogeeKilometers);
    }
}
