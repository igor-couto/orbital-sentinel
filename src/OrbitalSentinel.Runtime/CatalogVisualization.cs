using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.Runtime;

/// <summary>Shared visualization buffers; full paths are calculated only for viewed objects.</summary>
public sealed class CatalogVisualization(CatalogRepository catalog, IOrbitPropagator propagator,
    IOptions<PredictionOptions> options, ILogger<CatalogVisualization> logger, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim positionsGate = new(1);
    private readonly SemaphoreSlim trajectoryGate = new(1);
    private readonly SemaphoreSlim motionGate = new(1);
    private CatalogMotion? motion;
    private readonly MemoryCache paths = new(new MemoryCacheOptions { SizeLimit = 32 });
    private IReadOnlyList<OrbitSnapshot> orbits = [];
    private IReadOnlyList<ObjectPosition> positions = [];
    private DateTimeOffset catalogExpires, positionsExpires;

    public async Task<CatalogMotion> GetMotionAsync(CancellationToken ct)
    {
        await motionGate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var start = DateTimeOffset.FromUnixTimeSeconds(
                now.ToUnixTimeSeconds() / CatalogMotionPredictor.RefreshSeconds * CatalogMotionPredictor.RefreshSeconds);
            if (motion is not null && motion.StartUtc == start) return motion;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = await catalog.ListAllAsync(ct);
            var next = CatalogMotionPredictor.Build(snapshot, propagator, start, ct);
            motion = next;
            logger.LogInformation("Motion buffer: {Available}/{Total} objects in {ElapsedMs} ms, valid until {EndUtc}",
                next.Objects.Count, snapshot.Count, watch.ElapsedMilliseconds, next.EndUtc);
            return next;
        }
        finally { motionGate.Release(); }
    }

    public async Task<IReadOnlyList<ObjectPosition>> GetPositionsAsync(CancellationToken ct)
    {
        await positionsGate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (positionsExpires > now) return positions;
            if (catalogExpires <= now)
            {
                orbits = await catalog.ListAllAsync(ct);
                catalogExpires = now.AddSeconds(60);
            }
            var next = new List<ObjectPosition>(orbits.Count);
            var failed = 0;
            foreach (var orbit in orbits)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var point = TemeToEarthFixed.Convert(propagator.Propagate(orbit, now));
                    next.Add(new ObjectPosition(orbit.Object.Id, point.TimestampUtc, point.PositionEcefMeters));
                }
                catch (OrbitPropagationException) { failed++; }
            }
            if (failed > 0) logger.LogWarning("Current positions: {Available} available, {Failed} orbits could not propagate", next.Count, failed);
            positions = next;
            positionsExpires = now.AddSeconds(15);
            return positions;
        }
        finally { positionsGate.Release(); }
    }

    public async Task<Trajectory> GetTrajectoryAsync(OrbitSnapshot orbit, CancellationToken ct)
    {
        await trajectoryGate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            var key = (orbit.Object.Id, orbit.Version, start);
            if (paths.TryGetValue<Trajectory>(key, out var cached)) return cached!;
            var trajectory = propagator.Predict(orbit, start, TimeSpan.FromHours(options.Value.HorizonHours),
                TimeSpan.FromSeconds(options.Value.StepSeconds), ct);
            paths.Set(key, trajectory, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
            return trajectory;
        }
        finally { trajectoryGate.Release(); }
    }

    public void Dispose() { paths.Dispose(); positionsGate.Dispose(); trajectoryGate.Dispose(); motionGate.Dispose(); }
}
