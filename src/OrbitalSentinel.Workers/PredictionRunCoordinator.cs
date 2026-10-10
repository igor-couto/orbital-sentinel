using Microsoft.Extensions.Options;
using OrbitalSentinel.Runtime;

namespace OrbitalSentinel.Workers;

/// <summary>Only durable scheduled requests create scans; browser polling never schedules work.</summary>
public sealed class PredictionRunCoordinator(ConjunctionApplication application, IOptions<ConjunctionOptions> options,
    ILogger<PredictionRunCoordinator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.Value.Enabled)
                {
                    var now = DateTimeOffset.UtcNow;
                    var interval = TimeSpan.FromMinutes(options.Value.RunIntervalMinutes);
                    var slot = now.UtcTicks / interval.Ticks;
                    await application.CreateRealRunAsync($"scheduled:{slot}", stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Prediction-run scheduling failed; the request will be retried"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
