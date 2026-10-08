using OrbitalSentinel.Core;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.UnitTests;

public sealed class TemeToEarthFixedTests
{
    [Fact]
    public void GreenwichAngleAtJ2000_MatchesPublishedGmst()
    {
        var j2000 = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var angle = TemeToEarthFixed.GreenwichMeanSiderealTimeRadians(j2000);
        Assert.Equal(280.460618375, angle * 180 / Math.PI, 8);
    }

    [Fact]
    public void RotationUsesEarthFixedSignAndConvertsKilometersToMeters()
    {
        var j2000 = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var state = new StateVector(j2000, new CartesianVector(1, 0, 2), new CartesianVector(0, 1, 0));
        var point = TemeToEarthFixed.Convert(state);
        Assert.Equal(181.5596529467, point.PositionEcefMeters.X, 6);
        Assert.Equal(983.3799328797, point.PositionEcefMeters.Y, 6);
        Assert.Equal(2000, point.PositionEcefMeters.Z);
    }

    [Theory]
    [InlineData(1960)]
    [InlineData(2000)]
    [InlineData(2030)]
    public void RotationPreservesRadiusAndNormalizesSiderealAngle(int year)
    {
        var timestamp = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = new StateVector(timestamp, new CartesianVector(4000, -3000, 5000), new CartesianVector(0, 0, 0));
        var position = TemeToEarthFixed.Convert(state).PositionEcefMeters;
        var normSquared = position.X * position.X + position.Y * position.Y + position.Z * position.Z;
        Assert.Equal(50_000_000_000_000d, normSquared, 0.05);
        Assert.InRange(TemeToEarthFixed.GreenwichMeanSiderealTimeRadians(timestamp), 0, 2 * Math.PI);
    }
}
