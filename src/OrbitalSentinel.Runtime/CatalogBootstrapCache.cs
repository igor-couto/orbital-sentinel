using OrbitalSentinel.Core;

namespace OrbitalSentinel.Runtime;

/// <summary>Shares a compact catalog snapshot between visitors without tying its query to one request.</summary>
public sealed class CatalogBootstrapCache(
    Func<CancellationToken, Task<CatalogBootstrap>> load, TimeProvider clock)
{
    private readonly object gate = new();
    private CatalogBootstrap? snapshot;
    private DateTimeOffset expires;
    private Task<CatalogBootstrap>? pending;

    public Task<CatalogBootstrap> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (snapshot is not null && expires > clock.GetUtcNow()) return Task.FromResult(snapshot);
            if (pending is null || pending.IsCompleted)
            {
                // Observe a failed query even when every waiting browser disconnected.
                if (pending?.IsFaulted == true) _ = pending.Exception;
                pending = RefreshAsync();
            }
            // Abandoning one HTTP request must not cancel the shared query for other visitors.
            return pending.WaitAsync(cancellationToken);
        }
    }

    private async Task<CatalogBootstrap> RefreshAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var next = await load(timeout.Token);
        lock (gate)
        {
            snapshot = next;
            expires = clock.GetUtcNow().AddSeconds(30);
        }
        return next;
    }
}
