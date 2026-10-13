using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;

namespace OrbitalSentinel.Workers;

public sealed class CatalogMetadataWorker(SatelliteCatalogClient source, CatalogMetadataRepository metadata,
    JobScheduleRepository schedules, IOptions<CelesTrakOptions> catalogOptions,
    IOptions<SatelliteCatalogOptions> options, ILogger<CatalogMetadataWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var group in catalogOptions.Value.Groups.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!await schedules.TryBeginAsync($"satcat:{group.ToLowerInvariant()}",
                            TimeSpan.FromMinutes(options.Value.RequestIntervalMinutes), stoppingToken)) continue;
                    var records = await source.FetchGroupAsync(group, stoppingToken);
                    await metadata.SaveGroupAsync(group, records, DateTimeOffset.UtcNow, stoppingToken);
                    logger.LogInformation("Refreshed SATCAT metadata for {Group}: {Count} records", group, records.Count);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "SATCAT metadata refresh failed for {Group}; retaining cached metadata", group);
                }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
