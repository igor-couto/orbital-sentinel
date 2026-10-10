using Microsoft.Extensions.Options;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;

namespace OrbitalSentinel.Workers;

public sealed class ConjunctionWorker(ConjunctionRepository repository, IConjunctionScanner scanner,
    IOrbitPropagator propagator, IOptions<ConjunctionOptions> options, ILogger<ConjunctionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = options.Value.Enabled ? await repository.ClaimAsync(LeaseDuration, stoppingToken) : null;
                if (lease is null) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                else await ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Conjunction worker interrupted; durable jobs remain recoverable");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    public async Task ProcessAsync(ConjunctionScanLease lease, CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["PredictionRunId"] = lease.Run.Id });
        using var activity = Telemetry.Activities.StartActivity("conjunction.scan");
        activity?.SetTag("prediction.run.id", lease.Run.Id);
        using var calculation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        calculation.CancelAfter(TimeSpan.FromMinutes(options.Value.MaximumScanMinutes));
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewLeaseAsync(lease, calculation, renewal.Token);
        try
        {
            if (lease.Run.Context.AlgorithmVersion != ConjunctionAlgorithms.Current ||
                lease.Run.Context.PropagatorVersion != ConjunctionAlgorithms.PropagatorVersion)
                throw new NotSupportedException("The pinned run requires a different numerical model version; create a new run with this deployment.");
            var result = await Task.Run(() => scanner.Scan(lease.Run.Context, propagator, calculation.Token), calculation.Token);
            // Keep renewing until the publication transaction has verified the fencing token.
            if (!await repository.CompleteScanAsync(lease, result, calculation.Token))
            {
                logger.LogWarning("Scan result discarded because its lease is no longer current");
                return;
            }
            var metrics = result.Metrics;
            Telemetry.ConjunctionObjects.Record(metrics.ObjectCount);
            Telemetry.ConjunctionPairs.Record(metrics.PairCount);
            Telemetry.ConjunctionIntervals.Record(metrics.IntervalCount);
            Telemetry.ConjunctionCandidates.Record(metrics.CandidateCount);
            Telemetry.ConjunctionResults.Record(metrics.RefinedCount);
            Telemetry.ConjunctionDuration.Record(metrics.ScanDuration.TotalMilliseconds);
            Telemetry.RefinementDuration.Record(metrics.RefinementDuration.TotalMilliseconds);
            logger.LogInformation("Conjunction scan completed: {ObjectCount} objects, {PairCount} pairs, {IntervalCount} intervals, {CandidateCount} candidates, {ResultCount} results, {DurationMs} ms",
                metrics.ObjectCount, metrics.PairCount, metrics.IntervalCount, metrics.CandidateCount, metrics.RefinedCount, metrics.ScanDuration.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Telemetry.ConjunctionFailures.Add(1);
            logger.LogError(ex, "Conjunction scan failed; no successful empty result is published");
            await repository.FailAsync(lease, ex is OperationCanceledException ? "Calculation timed out or lost its lease." : ex.Message, stoppingToken);
        }
        finally
        {
            await renewal.CancelAsync();
            try { await heartbeat; }
            catch (OperationCanceledException) when (renewal.IsCancellationRequested) { }
        }
    }

    private async Task RenewLeaseAsync(ConjunctionScanLease lease, CancellationTokenSource calculation, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(ct))
                if (!await repository.RenewAsync(lease, LeaseDuration, ct))
                {
                    await calculation.CancelAsync();
                    return;
                }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Conjunction scan could not renew its lease");
            await calculation.CancelAsync();
        }
    }
}
