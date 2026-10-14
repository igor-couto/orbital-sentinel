using OrbitalSentinel.Core;

namespace OrbitalSentinel.Propagation;

public static class CatalogMotionPredictor
{
    public const int StepSeconds = 30;
    public const int WindowSeconds = 120;
    public const int RefreshSeconds = 60;

    public static CatalogMotion Build(IReadOnlyList<OrbitSnapshot> orbits, IOrbitPropagator propagator,
        DateTimeOffset startUtc, CancellationToken ct)
    {
        var objects = new List<ObjectMotion>(orbits.Count);
        foreach (var orbit in orbits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Initialization is reused across this object's samples, never shared concurrently.
                var evaluator = propagator.CreateEvaluator(orbit);
                var states = new double[(WindowSeconds / StepSeconds + 1) * 6];
                for (var i = 0; i < states.Length / 6; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var point = TemeToEarthFixed.Convert(evaluator.Evaluate(startUtc.AddSeconds(i * StepSeconds)));
                    var p = point.PositionEcefMeters;
                    var v = point.VelocityEcefMetersPerSecond!;
                    // Millimetre / millimetre-per-second quantization bounds JSON size.
                    states[i * 6] = Math.Round(p.X, 3);
                    states[i * 6 + 1] = Math.Round(p.Y, 3);
                    states[i * 6 + 2] = Math.Round(p.Z, 3);
                    states[i * 6 + 3] = Math.Round(v.X, 3);
                    states[i * 6 + 4] = Math.Round(v.Y, 3);
                    states[i * 6 + 5] = Math.Round(v.Z, 3);
                }
                objects.Add(new ObjectMotion(orbit.Object.Id, orbit.Version, states));
            }
            catch (OrbitPropagationException) { /* Exclude the whole object if any sample fails. */ }
        }
        return new CatalogMotion(Guid.NewGuid(), startUtc, startUtc.AddSeconds(WindowSeconds),
            StepSeconds, orbits.Count, objects);
    }
}
