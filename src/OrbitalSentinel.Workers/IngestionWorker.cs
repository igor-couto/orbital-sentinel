using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;

namespace OrbitalSentinel.Workers;

public sealed class IngestionWorker(
    CelesTrakClient source, CatalogRepository catalog, JobScheduleRepository schedules,
    ConjunctionApplication conjunctions,
    IOptions<CelesTrakOptions> options, IOptions<PredictionOptions> prediction,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await schedules.TryBeginAsync("celestrak-ingestion", TimeSpan.FromMinutes(options.Value.RequestIntervalMinutes), stoppingToken))
                {
                    using var activity = Telemetry.Activities.StartActivity("celestrak.ingest");
                    var imports = await source.FetchAsync(stoppingToken);
                    var changed = 0;
                    foreach (var import in imports)
                    {
                        if (await catalog.UpsertAsync(import, stoppingToken)) changed++;
                    }
                    Telemetry.IngestionChanges.Add(changed);
                    logger.LogInformation("CelesTrak ingestion completed: {ObjectsReceived} received, {OrbitsChanged} changed", imports.Count, changed);
                    await conjunctions.CreateRealRunAsync($"ingestion:{DateTimeOffset.UtcNow:yyyyMMddHHmm}", stoppingToken);
                }
                // A new prediction window is needed even when the upstream elements have not changed.
                if (await schedules.TryBeginAsync("trajectory-refresh", TimeSpan.FromMinutes(30), stoppingToken))
                    await catalog.EnqueueRefreshAsync(DateTimeOffset.UtcNow.AddHours(prediction.Value.HorizonHours / 2.0), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Telemetry.IngestionFailures.Add(1);
                logger.LogError(ex, "Ingestion/refresh cycle failed; durable schedule prevents repeated source downloads");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
