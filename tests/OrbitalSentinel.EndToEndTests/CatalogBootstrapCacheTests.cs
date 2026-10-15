using OrbitalSentinel.Core;
using OrbitalSentinel.Runtime;
using Xunit;

namespace OrbitalSentinel.EndToEndTests;

public sealed class CatalogBootstrapCacheTests
{
    [Fact]
    public async Task Concurrent_visitors_share_one_query_and_snapshot_until_expiry()
    {
        var clock = new Clock();
        var source = new TaskCompletionSource<CatalogBootstrap>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new CatalogBootstrapCache(_ => { calls++; return source.Task; }, clock);
        var readers = Enumerable.Range(0, 10).Select(_ => cache.GetAsync()).ToArray();
        Assert.Equal(1, calls);
        var first = Snapshot(1);
        source.SetResult(first);
        Assert.All(await Task.WhenAll(readers), item => Assert.Same(first, item));

        clock.Now = clock.Now.AddSeconds(29);
        Assert.Same(first, await cache.GetAsync());
        Assert.Equal(1, calls);
        clock.Now = clock.Now.AddSeconds(1);
        source = new TaskCompletionSource<CatalogBootstrap>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextReaders = Enumerable.Range(0, 10).Select(_ => cache.GetAsync()).ToArray();
        Assert.Equal(2, calls);
        var next = Snapshot(2);
        source.SetResult(next);
        Assert.All(await Task.WhenAll(nextReaders), item => Assert.Same(next, item));
    }

    [Fact]
    public async Task Canceling_one_visitor_does_not_cancel_the_shared_query_or_other_visitors()
    {
        var source = new TaskCompletionSource<CatalogBootstrap>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken queryToken = default;
        var cache = new CatalogBootstrapCache(token => { queryToken = token; return source.Task; }, new Clock());
        using var disconnected = new CancellationTokenSource();
        var first = cache.GetAsync(disconnected.Token);
        var other = cache.GetAsync();
        disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(queryToken.IsCancellationRequested);
        var expected = Snapshot(1);
        source.SetResult(expected);
        Assert.Same(expected, await other);
        Assert.Same(expected, await cache.GetAsync());
    }

    [Fact]
    public async Task Already_canceled_requests_do_not_start_a_query()
    {
        var calls = 0;
        var cache = new CatalogBootstrapCache(_ => { calls++; return Task.FromResult(Snapshot(1)); }, new Clock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(new CancellationToken(true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Failed_refresh_is_retried_and_does_not_replace_or_extend_the_previous_snapshot()
    {
        var clock = new Clock();
        var calls = 0;
        var cache = new CatalogBootstrapCache(_ => ++calls == 2
            ? Task.FromException<CatalogBootstrap>(new InvalidOperationException("Database unavailable"))
            : Task.FromResult(Snapshot(calls)), clock);
        Assert.Equal(1, (await cache.GetAsync()).Items[0].Version);
        clock.Now = clock.Now.AddSeconds(30);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync());
        Assert.Equal(3, (await cache.GetAsync()).Items[0].Version);
        Assert.Equal(3, calls);
        Assert.Equal(3, (await cache.GetAsync()).Items[0].Version);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Empty_catalog_is_cached_until_the_same_expiry()
    {
        var calls = 0;
        var cache = new CatalogBootstrapCache(_ => { calls++; return Task.FromResult(new CatalogBootstrap([], 0)); }, new Clock());
        Assert.Empty((await cache.GetAsync()).Items);
        Assert.Empty((await cache.GetAsync()).Items);
        Assert.Equal(1, calls);
    }

    private static CatalogBootstrap Snapshot(long version) => new(
        [new CatalogEntry(new CatalogObjectSummary(Guid.NewGuid(), 25544, "ISS", "1998-067A", "Payload"), version)], 1);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
