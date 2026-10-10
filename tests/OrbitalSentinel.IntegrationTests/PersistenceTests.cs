using Dapper;
using Npgsql;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;

namespace OrbitalSentinel.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class PersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CatalogRepository Catalog => new(fixture.DataSource);
    private OutboxRepository Outbox => new(fixture.DataSource);
    private TrajectoryRepository Trajectories => new(fixture.DataSource);
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migrations_are_concurrent_repeatable_and_reject_modified_checksums()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new DatabaseMigrator(fixture.DataSource).MigrateAsync()));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var migration = await connection.QueryFirstAsync<(string Name, string Checksum)>(
            "SELECT name,checksum FROM schema_migrations ORDER BY name;");
        await connection.ExecuteAsync("UPDATE schema_migrations SET checksum='tampered' WHERE name=@Name;", new { migration.Name });
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseMigrator(fixture.DataSource).MigrateAsync());
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE schema_migrations SET checksum=@Checksum WHERE name=@Name;",
                new { migration.Name, migration.Checksum });
        }
        await new DatabaseMigrator(fixture.DataSource).MigrateAsync();
    }

    [Fact]
    public async Task Concurrent_imports_create_one_identity_version_and_event_then_metadata_does_not_emit()
    {
        var import = Import();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Catalog.UpsertAsync(import)));
        Assert.Single(results, x => x);
        Assert.False(await Catalog.UpsertAsync(import with { Name = "Renamed spacecraft" }));
        var page = await Catalog.ListAsync(1, 20, "Renamed");
        var snapshot = Assert.Single(page.Items);
        Assert.Equal(1L, snapshot.Version);
        Assert.Equal(25544L, snapshot.Object.NoradCatalogId);
        Assert.NotEqual(Guid.Empty, snapshot.Object.Id);
        Assert.Equal(1, await Count("orbital_objects"));
        Assert.Equal(1, await Count("orbital_element_sets"));
        Assert.Equal(1, await Count("outbox_messages"));
    }

    [Fact]
    public async Task Classification_updates_existing_unknown_objects_without_new_orbital_revision()
    {
        var import = Import(34146) with { Name = "IRIDIUM 33 DEB" };
        await Catalog.UpsertAsync(import);
        var original = Assert.Single((await Catalog.ListAsync(1, 20, null)).Items);

        Assert.False(await Catalog.UpsertAsync(import with { ObjectType = "Debris" }));
        var updated = await Catalog.GetAsync(original.Object.Id);
        Assert.Equal("Debris", updated!.Object.ObjectType);
        Assert.Equal(original.Version, updated.Version);
        Assert.Equal(original.Fingerprint, updated.Fingerprint);
        Assert.Equal(1, await Count("orbital_objects"));
        Assert.Equal(1, await Count("orbital_element_sets"));
        Assert.Equal(1, await Count("outbox_messages"));
    }

    [Fact]
    public async Task Older_epoch_cannot_overwrite_and_versions_are_preserved()
    {
        var import = Import();
        await Catalog.UpsertAsync(import);
        var original = Assert.Single((await Catalog.ListAsync(1, 20, null)).Items);
        Assert.True(await Catalog.UpsertAsync(import with { Elements = import.Elements with { EpochUtc = Epoch.AddHours(2) } }));
        Assert.False(await Catalog.UpsertAsync(import with { Name = "Stale metadata", Elements = import.Elements with { MeanAnomalyDegrees = 8 } }));
        var latest = await Catalog.GetAsync(original.Object.Id);
        Assert.Equal(2, latest!.Version);
        Assert.Equal(import.Name, latest.Object.Name);
        Assert.Equal(Epoch, (await Catalog.GetVersionAsync(original.Object.Id, 1))!.Elements.EpochUtc);
        Assert.Equal(2, await Count("outbox_messages"));
    }

    [Fact]
    public async Task Internal_identity_works_without_norad()
    {
        var id = Guid.NewGuid();
        await Catalog.UpsertAsync(Import() with { InternalId = id, NoradCatalogId = null });
        Assert.False(await Catalog.UpsertAsync(Import() with { InternalId = id, NoradCatalogId = null }));
        await Catalog.UpsertAsync(Import() with { InternalId = Guid.NewGuid(), NoradCatalogId = null });
        Assert.Null((await Catalog.GetAsync(id))!.Object.NoradCatalogId);
        Assert.Equal(2, await Count("orbital_objects"));
    }

    [Fact]
    public async Task Catalog_write_rolls_back_if_outgoing_event_insert_fails()
    {
        await ForceOutboxFailure();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => Catalog.UpsertAsync(Import()));
            Assert.Equal(0, await Count("orbital_objects"));
            Assert.Equal(0, await Count("orbital_element_sets"));
        }
        finally { await RemoveOutboxFailure(); }
    }

    [Fact]
    public async Task Claims_do_not_overlap_and_expiry_and_tokens_fence_acknowledgements()
    {
        foreach (var id in Enumerable.Range(1, 6)) await Catalog.UpsertAsync(Import(id));
        var claims = await Task.WhenAll(Enumerable.Range(1, 4).Select(_ => Outbox.ClaimAsync(2, TimeSpan.FromMinutes(1))));
        var all = claims.SelectMany(x => x).ToArray();
        Assert.Equal(6, all.Length);
        Assert.Equal(6, all.Select(x => x.Id).Distinct().Count());
        var first = all[0];
        Assert.False(await Outbox.MarkPublishedAsync(first.Id, Guid.NewGuid()));
        await Sql("UPDATE outbox_messages SET lease_expires_at=now()-interval '1 second' WHERE id=@id;", new { id = first.Id });
        Assert.False(await Outbox.MarkPublishedAsync(first.Id, first.LeaseToken));
        Assert.False(await Outbox.MarkFailedAsync(first.Id, first.LeaseToken, "late failure"));
        var replacement = Assert.Single(await Outbox.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.Equal(first.Id, replacement.Id);
        Assert.NotEqual(first.LeaseToken, replacement.LeaseToken);
        Assert.False(await Outbox.MarkPublishedAsync(first.Id, first.LeaseToken));
        Assert.True(await Outbox.MarkPublishedAsync(replacement.Id, replacement.LeaseToken));
    }

    [Fact]
    public async Task Poison_events_stop_after_finite_attempts_without_starving_other_objects()
    {
        var first = Import(1);
        await Catalog.UpsertAsync(first);
        await Catalog.UpsertAsync(first with { Elements = first.Elements with { EpochUtc = Epoch.AddMinutes(1) } });
        await Catalog.UpsertAsync(Import(2));
        var initial = await Outbox.ClaimAsync(20, TimeSpan.FromMinutes(1));
        Assert.Equal(2, initial.Count); // version two waits behind version one
        var objectOne = Assert.Single((await Catalog.ListAsync(1, 20, "1")).Items).Object.Id;
        var poison = initial.Single(x => x.PartitionKey == objectOne.ToString("D"));
        var healthy = initial.Single(x => x.Id != poison.Id);
        Assert.True(await Outbox.MarkPublishedAsync(healthy.Id, healthy.LeaseToken));
        for (var attempt = 1; attempt <= OutboxRepository.MaxAttempts; attempt++)
        {
            Assert.Equal(attempt, poison.RetryCount);
            Assert.True(await Outbox.MarkFailedAsync(poison.Id, poison.LeaseToken, "Permanent broker rejection"));
            if (attempt == OutboxRepository.MaxAttempts) break;
            Assert.Empty(await Outbox.ClaimAsync(20, TimeSpan.FromMinutes(1)));
            await Sql("UPDATE outbox_messages SET next_attempt_at=now()-interval '1 second' WHERE id=@id;", new { id = poison.Id });
            poison = Assert.Single(await Outbox.ClaimAsync(20, TimeSpan.FromMinutes(1)));
        }
        var next = Assert.Single(await Outbox.ClaimAsync(20, TimeSpan.FromMinutes(1)));
        Assert.NotEqual(poison.Id, next.Id);
        Assert.Equal(poison.PartitionKey, next.PartitionKey);
        Assert.Equal(1, await Count("outbox_messages", "WHERE dead_lettered_at IS NOT NULL"));
    }

    [Fact]
    public async Task Trajectory_inbox_and_event_are_atomic_duplicates_are_ignored_and_refresh_advances()
    {
        await Catalog.UpsertAsync(Import());
        var snapshot = Assert.Single((await Catalog.ListAsync(1, 20, null)).Items);
        var trajectory = MakeTrajectory(snapshot.Object.Id, 1);
        var eventId = Guid.NewGuid();
        await ForceOutboxFailure();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => Trajectories.SaveAsync(eventId, trajectory));
            Assert.Equal(0, await Count("processed_messages"));
            Assert.Equal(0, await Count("trajectory_predictions"));
        }
        finally { await RemoveOutboxFailure(); }
        Assert.True(await Trajectories.SaveAsync(eventId, trajectory));
        Assert.False(await Trajectories.SaveAsync(eventId, trajectory));
        Assert.False(await Trajectories.SaveAsync(Guid.NewGuid(), trajectory));
        Assert.Equal(1, await Count("trajectory_predictions"));
        Assert.Equal(2, await Count("outbox_messages"));
        var refreshed = MakeTrajectory(snapshot.Object.Id, 1, Epoch.AddHours(2));
        Assert.True(await Trajectories.SaveAsync(Guid.NewGuid(), refreshed));
        Assert.Equal(refreshed.StartUtc, (await Trajectories.GetAsync(snapshot.Object.Id))!.StartUtc);
        Assert.False(await Trajectories.SaveAsync(Guid.NewGuid(), trajectory));
    }

    [Fact]
    public async Task Stale_trajectory_is_recorded_in_inbox_without_cache_and_unknown_version_rolls_back()
    {
        var import = Import();
        await Catalog.UpsertAsync(import);
        var id = Assert.Single((await Catalog.ListAsync(1, 20, null)).Items).Object.Id;
        await Catalog.UpsertAsync(import with { Elements = import.Elements with { EpochUtc = Epoch.AddHours(1) } });
        Assert.False(await Trajectories.SaveAsync(Guid.NewGuid(), MakeTrajectory(id, 1)));
        Assert.Null(await Trajectories.GetAsync(id));
        Assert.Equal(1, await Count("processed_messages"));
        var unknownEvent = Guid.NewGuid();
        await Assert.ThrowsAsync<PostgresException>(() => Trajectories.SaveAsync(unknownEvent, MakeTrajectory(id, 3)));
        Assert.Equal(1, await Count("processed_messages"));
        Assert.True(await Trajectories.SaveAsync(Guid.NewGuid(), MakeTrajectory(id, 2)));
        await Catalog.UpsertAsync(import with { Elements = import.Elements with { EpochUtc = Epoch.AddHours(2) } });
        Assert.Null(await Trajectories.GetAsync(id));
    }

    [Fact]
    public async Task Position_queries_return_actual_sample_times_and_omit_expired_predictions()
    {
        await Catalog.UpsertAsync(Import());
        var id = Assert.Single((await Catalog.ListAsync(1, 20, null)).Items).Object.Id;
        await Trajectories.SaveAsync(Guid.NewGuid(), MakeTrajectory(id, 1));
        var position = Assert.Single(await Trajectories.ListPositionsAsync(Epoch.AddSeconds(52)));
        Assert.Equal(Epoch.AddMinutes(1), position.TimestampUtc);
        Assert.Equal(7_001_000, position.PositionEcefMeters.X);
        Assert.Empty(await Trajectories.ListPositionsAsync(Epoch.AddHours(2)));
    }

    [Fact]
    public async Task Durable_job_schedule_claims_once_across_replicas()
    {
        var scheduler = new JobScheduleRepository(fixture.DataSource);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => scheduler.TryBeginAsync("celestrak-ingestion", TimeSpan.FromHours(2))));
        Assert.Single(claims, x => x);
        Assert.False(await new JobScheduleRepository(fixture.DataSource).TryBeginAsync("celestrak-ingestion", TimeSpan.FromHours(2)));
        await Sql("UPDATE job_schedules SET next_run_at=now()-interval '1 second';");
        Assert.True(await scheduler.TryBeginAsync("celestrak-ingestion", TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task Refresh_uses_explicit_command_and_deduplicates_pending_work()
    {
        await Catalog.UpsertAsync(Import());
        Assert.Equal(0, await Catalog.EnqueueRefreshAsync(Epoch.AddHours(18)));
        var initial = Assert.Single(await Outbox.ClaimAsync(10, TimeSpan.FromMinutes(1)));
        await Outbox.MarkPublishedAsync(initial.Id, initial.LeaseToken);
        var refreshes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Catalog.EnqueueRefreshAsync(Epoch.AddHours(18))));
        Assert.Equal(1, refreshes.Sum());
        var command = Assert.Single(await Outbox.ClaimAsync(10, TimeSpan.FromMinutes(1)));
        Assert.Equal(TrajectoryRefreshRequestedEvent.TypeName, command.EventType);
    }

    [Fact]
    public async Task Quarantine_is_durable_idempotent_and_atomic_with_dead_letter_event()
    {
        var failures = new ConsumerFailureRepository(fixture.DataSource);
        await ForceOutboxFailure();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => failures.QuarantineAsync("orbits", 0, 25, "key", "broken", "invalid JSON"));
            Assert.Equal(0, await Count("consumer_failures"));
        }
        finally { await RemoveOutboxFailure(); }
        await failures.QuarantineAsync("orbits", 0, 25, "key", "broken", "invalid JSON");
        await failures.QuarantineAsync("orbits", 0, 25, "key", "broken", "invalid JSON");
        Assert.Equal(1, await Count("consumer_failures"));
        Assert.Equal(1, await Count("outbox_messages"));
        Assert.Equal(ConsumerDeadLetterEvent.TypeName, Assert.Single(await Outbox.ClaimAsync(10, TimeSpan.FromMinutes(1))).EventType);
    }

    private static CatalogImport Import(long norad = 25544) => new(null, norad, $"Object {norad}", "1998-067A", "Unknown",
        new OrbitalElements(Epoch, 15.5, 0.0001, 51.6, 20, 30, 40, 0.00003, 0, 0));

    private static Trajectory MakeTrajectory(Guid id, long version, DateTimeOffset? start = null)
    {
        var timestamp = start ?? Epoch;
        return new Trajectory(id, version, timestamp, timestamp.AddMinutes(1), 60,
        [new VisualizationPoint(timestamp, new CartesianVector(7_000_000, 0, 0)),
         new VisualizationPoint(timestamp.AddMinutes(1), new CartesianVector(7_001_000, 0, 0))]);
    }

    private Task ForceOutboxFailure() => Sql("ALTER TABLE outbox_messages ADD CONSTRAINT forced_failure CHECK (false) NOT VALID;");
    private Task RemoveOutboxFailure() => Sql("ALTER TABLE outbox_messages DROP CONSTRAINT forced_failure;");
    private async Task Sql(string sql, object? parameters = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
    private async Task<int> Count(string table, string predicate = "")
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} {predicate};");
    }
}
