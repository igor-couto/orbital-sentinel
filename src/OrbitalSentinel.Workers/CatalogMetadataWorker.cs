using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;

namespace OrbitalSentinel.Workers;

public sealed class CatalogMetadataWorker(SatelliteCatalogClient source, CatalogMetadataRepository metadata,
    JobScheduleRepository schedules, CelesTrakGroups groups,
    IOptions<SatelliteCatalogOptions> options, ILogger<CatalogMetadataWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await schedules.TryBeginAsync("satcat:group-cycle", TimeSpan.FromMinutes(120), stoppingToken))
                {
                    foreach (var group in await groups.GetAsync(stoppingToken))
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
                            if (exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests }) break;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "SATCAT scheduling failed; retrying while retaining cached metadata");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
