using OrbitalSentinel.Core;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.UnitTests;

public sealed class CatalogMotionTests
{
    [Fact]
    public void BufferIncludesBothEndpointsAndPreservesObjectRevision()
    {
        var orbit = ReferenceOrbit.Snapshot();
        var result = CatalogMotionPredictor.Build([orbit], new Sgp4OrbitPropagator(), orbit.Elements.EpochUtc, CancellationToken.None);
        var item = Assert.Single(result.Objects);
        Assert.Equal(orbit.Object.Id, item.OrbitalObjectId);
        Assert.Equal(orbit.Version, item.OrbitVersion);
        Assert.Equal(120, (result.EndUtc - result.StartUtc).TotalSeconds);
        Assert.Equal(30, item.States.Length);
        Assert.All(item.States, n => Assert.True(double.IsFinite(n)));
    }

    [Theory]
    [InlineData(15.5, 0.0004)]
    [InlineData(1.0027, 0.0001)]
    [InlineData(1.5751094, 0.74299681)]
    public void InterpolationTracksDirectSgp4WithinTenMetres(double meanMotion, double eccentricity)
    {
        var initial = ReferenceOrbit.Snapshot();
        var orbit = initial with { Elements = initial.Elements with { MeanMotionRevolutionsPerDay = meanMotion, Eccentricity = eccentricity } };
        var propagator = new Sgp4OrbitPropagator();
        // Test several locations, including the fast perigee of an eccentric orbit.
        for (var phase = 0; phase < 12; phase++)
        {
            var start = orbit.Elements.EpochUtc.AddMinutes(phase * 1440 / meanMotion / 12);
            var buffer = CatalogMotionPredictor.Build([orbit], propagator, start, CancellationToken.None);
            var s = Assert.Single(buffer.Objects).States;
            for (var seconds = 1; seconds < 120; seconds += 7)
            {
                var offset = seconds / 30 * 6;
                var t = (seconds % 30) / 30d; var t2 = t * t; var t3 = t2 * t;
                var w = new[] { 2 * t3 - 3 * t2 + 1, (t3 - 2 * t2 + t) * 30, -2 * t3 + 3 * t2, (t3 - t2) * 30 };
                var expected = TemeToEarthFixed.Convert(propagator.Propagate(orbit, start.AddSeconds(seconds))).PositionEcefMeters;
                var actual = Enumerable.Range(0, 3).Select(axis => w[0] * s[offset + axis] + w[1] * s[offset + axis + 3] + w[2] * s[offset + axis + 6] + w[3] * s[offset + axis + 9]).ToArray();
                var distance = Math.Sqrt(Math.Pow(expected.X - actual[0], 2) + Math.Pow(expected.Y - actual[1], 2) + Math.Pow(expected.Z - actual[2], 2));
                Assert.InRange(distance, 0, 10);
            }
        }
    }

    [Fact]
    public void FailedObjectIsExcludedAndCancellationIsHonored()
    {
        var orbit = ReferenceOrbit.Snapshot();
        var result = CatalogMotionPredictor.Build([orbit], new FailingPropagator(), orbit.Elements.EpochUtc, CancellationToken.None);
        Assert.Equal(1, result.CatalogCount);
        Assert.Empty(result.Objects);
        Assert.Throws<OperationCanceledException>(() => CatalogMotionPredictor.Build([orbit], new Sgp4OrbitPropagator(), orbit.Elements.EpochUtc, new CancellationToken(true)));
    }

    private sealed class FailingPropagator : IOrbitPropagator
    {
        public StateVector Propagate(OrbitSnapshot orbit, DateTimeOffset timestampUtc) => throw new OrbitPropagationException(orbit.Object.Id, timestampUtc, "Unavailable");
        public Trajectory Predict(OrbitSnapshot orbit, DateTimeOffset startUtc, TimeSpan horizon, TimeSpan step, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
