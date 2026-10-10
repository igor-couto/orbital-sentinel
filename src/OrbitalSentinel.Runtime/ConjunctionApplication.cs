using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.Runtime;

public sealed record ConjunctionVisualization(Guid AssessmentId, Guid PredictionRunId, string Source,
    DateTimeOffset TimeOfClosestApproachUtc, OrbitSnapshot Primary, OrbitSnapshot Secondary,
    Trajectory PrimaryTrajectory, Trajectory SecondaryTrajectory,
    CartesianVector PrimaryPositionEcefMeters, CartesianVector SecondaryPositionEcefMeters);

/// <summary>Application operations joining immutable manifests to the numerical adapter.</summary>
public sealed class ConjunctionApplication(ConjunctionRepository repository, IOrbitPropagator propagator,
    IOptions<PredictionOptions> prediction, IOptions<ConjunctionOptions> options) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 });

    public void Dispose() => cache.Dispose();

    public Task<PredictionRunRecord?> CreateRealRunAsync(string requestKey, CancellationToken ct)
    {
        if (!options.Value.Enabled) return Task.FromResult<PredictionRunRecord?>(null);
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        return repository.CreateRealRunAsync(start, TimeSpan.FromHours(prediction.Value.HorizonHours),
            TimeSpan.FromSeconds(options.Value.SamplingIntervalSeconds), options.Value.ToSettings(), "real:" + requestKey, ct);
    }

    public Task<PredictionRunRecord> CreateSimulationAsync(Guid requestId, CancellationToken ct)
    {
        var scenario = CrossingOrbitsSimulation.Create();
        var context = new PredictionRunContext(Guid.NewGuid(), DateTimeOffset.UtcNow, scenario.ClockUtc,
            scenario.ClockUtc.AddHours(24), 60, "Simulation", scenario.Id, ConjunctionAlgorithms.Current,
            Sgp4OrbitPropagator.Version, options.Value.ToSettings(), scenario.Objects);
        return repository.CreateSimulationRunAsync(context, $"simulation:{scenario.Id:D}:v{scenario.Version}:{requestId:D}", ct);
    }

    public async Task<ConjunctionVisualization?> GetVisualizationAsync(Guid assessmentId, CancellationToken ct)
    {
        var cacheKey = $"conjunction-visualization:{assessmentId:D}";
        if (cache.TryGetValue<ConjunctionVisualization>(cacheKey, out var found)) return found;
        var assessment = await repository.GetAssessmentAsync(assessmentId, ct);
        if (assessment is null) return null;
        var run = await repository.GetRunAsync(assessment.PredictionRunId, ct)
            ?? throw new InvalidOperationException("The assessment input manifest is unavailable.");
        if (run.Context.PropagatorVersion != Sgp4OrbitPropagator.Version)
            throw new NotSupportedException("This historical prediction used a different propagator version.");
        var conjunction = await repository.GetAsync(assessment.ConjunctionId, ct)
            ?? throw new InvalidOperationException("The assessed encounter is unavailable.");
        var primary = run.Context.Objects.Single(x => x.Object.Id == conjunction.PrimaryObjectId);
        var secondary = run.Context.Objects.Single(x => x.Object.Id == conjunction.SecondaryObjectId);
        var first = Path(primary, run.Context, assessment.PrimaryState, ct);
        var second = Path(secondary, run.Context, assessment.SecondaryState, ct);
        var visualization = new ConjunctionVisualization(assessment.Id, run.Id, run.Context.Source,
            assessment.TimeOfClosestApproachUtc, primary, secondary, first, second,
            ToVisualization(assessment.PrimaryState).PositionEcefMeters,
            ToVisualization(assessment.SecondaryState).PositionEcefMeters);
        // Run inputs are immutable; this bounded-duration cache is optional and never the scientific source.
        cache.Set(cacheKey, visualization, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
        return visualization;
    }

    private Trajectory Path(OrbitSnapshot orbit, PredictionRunContext run, TemeStateSi state, CancellationToken ct)
    {
        var path = propagator.Predict(orbit, run.PredictionStartUtc, run.PredictionEndUtc - run.PredictionStartUtc,
            TimeSpan.FromSeconds(run.StepSeconds), ct);
        var exact = ToVisualization(state);
        return path with { Points = path.Points.Where(x => x.TimestampUtc != state.TimestampUtc)
            .Append(exact).OrderBy(x => x.TimestampUtc).ToArray() };
    }

    private static VisualizationPoint ToVisualization(TemeStateSi state) => TemeToEarthFixed.Convert(new StateVector(
        state.TimestampUtc, new CartesianVector(state.PositionMeters.X / 1000, state.PositionMeters.Y / 1000, state.PositionMeters.Z / 1000),
        new CartesianVector(state.VelocityMetersPerSecond.X / 1000, state.VelocityMetersPerSecond.Y / 1000, state.VelocityMetersPerSecond.Z / 1000)));
}
