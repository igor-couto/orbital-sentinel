using OrbitalSentinel.Core;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.UnitTests;

public sealed class ConjunctionSimulationTests
{
    [Fact]
    public void CrossingScenarioProducesReproducibleSgp4EncountersWithoutCatalogIdentifiers()
    {
        var scenario = CrossingOrbitsSimulation.Create();
        var run = new PredictionRunContext(Guid.NewGuid(), scenario.ClockUtc, scenario.ClockUtc,
            scenario.ClockUtc.AddHours(24), 60, "Simulation", scenario.Id,
            ConjunctionAlgorithms.Current, Sgp4OrbitPropagator.Version, new(), scenario.Objects);
        var scanner = new ConjunctionScanner();
        var propagator = new Sgp4OrbitPropagator();
        var result = scanner.Scan(run, propagator);
        Assert.NotEmpty(result.Encounters);
        var first = result.Encounters[0];
        Assert.InRange((first.TimeOfClosestApproachUtc - scenario.ClockUtc).TotalMinutes, 4, 6);
        Assert.InRange(first.MissDistanceMeters, 0, 10_000);
        Assert.InRange(first.RelativeVelocityMetersPerSecond, 8_000, 14_000);
        Assert.All(scenario.Objects, orbit =>
        {
            Assert.Null(orbit.Object.NoradCatalogId);
            Assert.Equal("SIMULATION", orbit.Object.ObjectType);
        });
        var repeated = scanner.Scan(run with { Id = Guid.NewGuid() }, propagator);
        Assert.Equal(result.Encounters, repeated.Encounters);
    }

    [Fact]
    public void ReusedEvaluatorMatchesIndependentPropagationInNonChronologicalOrder()
    {
        var propagator = new Sgp4OrbitPropagator();
        // Deep-space branch, whose library initialization contains mutable integration state.
        var orbit = ReferenceOrbit.Snapshot(ReferenceOrbit.Elements with
        {
            MeanMotionRevolutionsPerDay = 1.0027,
            Eccentricity = 0.001,
            InclinationDegrees = 3
        });
        var evaluator = propagator.CreateEvaluator(orbit);
        foreach (var minutes in new[] { 0, 1440, 60, 1400, 30, -60, 1000, 15 })
        {
            var time = orbit.Elements.EpochUtc.AddMinutes(minutes);
            var expected = propagator.Propagate(orbit, time);
            var actual = evaluator.Evaluate(time);
            Assert.InRange(Distance(expected.PositionKm, actual.PositionKm), 0, 1e-8);
            Assert.InRange(Distance(expected.VelocityKmPerSecond, actual.VelocityKmPerSecond), 0, 1e-10);
        }
    }

    private static double Distance(CartesianVector a, CartesianVector b) => Math.Sqrt(
        Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
}
