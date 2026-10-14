using OrbitalSentinel.Core;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.UnitTests;

public sealed class OrbitViewGeometryTests
{
    [Fact]
    public void GeometryMatchesDirectTemeStatesInOneCommonOrientation()
    {
        var orbit = ReferenceOrbit.Snapshot();
        var propagator = new Sgp4OrbitPropagator();
        var start = orbit.Elements.EpochUtc;
        var original = propagator.Predict(orbit, start, TimeSpan.FromHours(24), TimeSpan.FromMinutes(1), CancellationToken.None);
        var result = OrbitViewGeometry.AddTo(original);
        var theta = TemeToEarthFixed.GreenwichMeanSiderealTimeRadians(start);
        var (sin, cos) = Math.SinCos(theta);
        foreach (var point in result.Points.Where((_, index) => index % 47 == 0))
        {
            var state = propagator.Propagate(orbit, point.TimestampUtc);
            var view = Assert.IsType<OrbitViewPoint>(point.OrbitView);
            AssertVector(new CartesianVector(
                1000 * (cos * state.PositionKm.X + sin * state.PositionKm.Y),
                1000 * (-sin * state.PositionKm.X + cos * state.PositionKm.Y), 1000 * state.PositionKm.Z), view.PositionMeters, 0.001);
            AssertVector(new CartesianVector(
                1000 * (cos * state.VelocityKmPerSecond.X + sin * state.VelocityKmPerSecond.Y),
                1000 * (-sin * state.VelocityKmPerSecond.X + cos * state.VelocityKmPerSecond.Y), 1000 * state.VelocityKmPerSecond.Z), view.VelocityMetersPerSecond!, 0.000001);
            var (s, c) = Math.SinCos(view.RotationToEarthFixedRadians);
            var p = view.PositionMeters;
            AssertVector(point.PositionEcefMeters, new CartesianVector(c * p.X - s * p.Y, s * p.X + c * p.Y, p.Z), 0.000001);
        }
        Assert.All(original.Points, point => Assert.Null(point.OrbitView));
        Assert.Equal(original.Points.Select(p => p.PositionEcefMeters), result.Points.Select(p => p.PositionEcefMeters));
    }

    [Fact]
    public void StationaryEarthRelativePointBecomesAnOrbitAndOrientationsDoNotWrap()
    {
        var start = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 49).Select(i => new VisualizationPoint(start.AddHours(i),
            new CartesianVector(42164000, 0, 0))).ToArray();
        var path = new Trajectory(Guid.NewGuid(), 1, start, start.AddHours(48), 3600, points);
        var result = OrbitViewGeometry.AddTo(path);
        Assert.All(result.Points, p => Assert.Null(p.OrbitView!.VelocityMetersPerSecond));
        Assert.Equal(0, result.Points[0].OrbitView!.RotationToEarthFixedRadians);
        for (var i = 1; i < result.Points.Count; i++)
            Assert.InRange(result.Points[i].OrbitView!.RotationToEarthFixedRadians - result.Points[i - 1].OrbitView!.RotationToEarthFixedRadians, -0.264, -0.26);
        Assert.True(result.Points[^1].OrbitView!.RotationToEarthFixedRadians < -4 * Math.PI);
        // A quarter day later, the shape has passed around the side of Earth.
        Assert.InRange(result.Points[6].OrbitView!.PositionMeters.Y, 42000000, 42200000);
        Assert.InRange(result.Points[12].OrbitView!.PositionMeters.X, -42200000, -42000000);
    }

    private static void AssertVector(CartesianVector expected, CartesianVector actual, double tolerance)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, tolerance);
    }
}
