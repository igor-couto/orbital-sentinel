using System.Text.Json;
using Dapper;
using Npgsql;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;

namespace OrbitalSentinel.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class ConjunctionPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ScenarioId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PrimaryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecondaryId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private ConjunctionRepository Repository => new(fixture.DataSource);
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Real_run_is_atomic_deduplicated_and_pins_elements_without_storing_samples()
    {
        var catalog = new CatalogRepository(fixture.DataSource);
        var a = Import(PrimaryId, 1);
        await catalog.UpsertAsync(a);
        await catalog.UpsertAsync(Import(SecondaryId, 2));
        var runs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Repository.CreateRealRunAsync(
            Epoch, TimeSpan.FromHours(24), TimeSpan.FromSeconds(60), new ConjunctionSettings(), "real:hour")));
        Assert.Single(runs.Select(x => x!.Id).Distinct());
        var run = runs[0]!;
        Assert.Equal(2, run.Context.Objects.Count);
        Assert.Equal(1, await Count("prediction_runs"));
        Assert.Equal(1, await CountEvents(PredictionRunCompletedEvent.TypeName));
        Assert.Equal(0, await Count("conjunction_scan_jobs"));
        await catalog.UpsertAsync(a with { Name = "New name", Elements = a.Elements with { EpochUtc = Epoch.AddHours(1) } });
        var pinned = (await Repository.GetRunAsync(run.Id))!.Context.Objects.Single(x => x.Object.Id == PrimaryId);
        Assert.Equal(1, pinned.Version);
        Assert.Equal(Epoch, pinned.Elements.EpochUtc);
        Assert.Equal("Object 1", pinned.Object.Name);
        var manifest = await Scalar<string>("SELECT manifest::text FROM prediction_runs;");
        Assert.DoesNotContain("positionMeters", manifest);
        Assert.DoesNotContain("trajectory", manifest);
    }

    [Fact]
    public async Task Real_run_persists_every_object_beyond_the_previous_500_object_limit()
    {
        var catalog = new CatalogRepository(fixture.DataSource);
        for (var index = 1; index <= 505; index++)
            await catalog.UpsertAsync(Import(Guid.NewGuid(), index));
        var run = await Repository.CreateRealRunAsync(Epoch, TimeSpan.FromHours(24),
            TimeSpan.FromSeconds(60), new ConjunctionSettings(), "real:unlimited");
        Assert.NotNull(run);
        Assert.Equal(505, run.Context.Objects.Count);
        var persisted = await Repository.GetRunAsync(run.Id);
        Assert.Equal(505, persisted!.Context.Objects.Count);
        Assert.Equal(505, persisted.Context.Objects.Select(x => x.Object.Id).Distinct().Count());
    }

    [Fact]
    public async Task Valid_completed_screening_remains_available_while_a_newer_run_is_pending_but_expired_results_do_not()
    {
        var catalog = new CatalogRepository(fixture.DataSource);
        await catalog.UpsertAsync(Import(PrimaryId, 1));
        await catalog.UpsertAsync(Import(SecondaryId, 2));
        var first = (await Repository.CreateRealRunAsync(Epoch, TimeSpan.FromHours(24),
            TimeSpan.FromSeconds(60), new ConjunctionSettings(), "first"))!;
        await Repository.TryEnqueueScanAsync(await CompletedEvent(first.Id));
        var lease = (await Repository.ClaimAsync(TimeSpan.FromMinutes(1)))!;
        Assert.True(await Repository.CompleteScanAsync(lease, Result()));
        await Repository.CreateRealRunAsync(Epoch.AddHours(1), TimeSpan.FromHours(24),
            TimeSpan.FromSeconds(60), new ConjunctionSettings(), "next");
        Assert.Equal(first.Id, (await Repository.GetLatestCompletedRealRunAsync(Epoch.AddHours(2)))!.Id);
        Assert.Null(await Repository.GetLatestCompletedRealRunAsync(Epoch.AddDays(2)));
    }

    [Fact]
    public async Task Run_creation_rolls_back_manifest_and_scope_when_outbox_fails()
    {
        await ForceOutboxFailure();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => Repository.CreateSimulationRunAsync(Context(), "broken"));
            Assert.Equal(0, await Count("prediction_runs"));
            Assert.Equal(0, await Count("conjunction_scopes"));
        }
        finally { await RemoveOutboxFailure(); }
    }

    [Fact]
    public async Task Synthetic_inputs_stay_outside_real_catalog_and_repeated_event_schedules_once()
    {
        var run = await Create("simulation");
        var message = await CompletedEvent(run.Id);
        var scheduled = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Repository.TryEnqueueScanAsync(message)));
        Assert.Single(scheduled, x => x);
        Assert.False(await Repository.TryEnqueueScanAsync(message with { EventId = Guid.NewGuid() }));
        Assert.Equal(1, await Count("conjunction_scan_jobs"));
        Assert.Equal(2, await Count("processed_messages"));
        Assert.Equal(0, await Count("orbital_objects"));
        Assert.Equal(0, await Count("orbital_element_sets"));
        Assert.Equal(0, await Count("trajectory_predictions"));
        Assert.Equal("Pending", (await Repository.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task Scheduling_inbox_rolls_back_with_job_failure()
    {
        var run = await Create("simulation");
        var message = await CompletedEvent(run.Id);
        await Sql("ALTER TABLE conjunction_scan_jobs ADD CONSTRAINT forced_job_failure CHECK (false) NOT VALID;");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => Repository.TryEnqueueScanAsync(message));
            Assert.Equal(0, await Count("processed_messages"));
        }
        finally { await Sql("ALTER TABLE conjunction_scan_jobs DROP CONSTRAINT forced_job_failure;"); }
        Assert.True(await Repository.TryEnqueueScanAsync(message));
    }

    [Fact]
    public async Task First_assessment_identifies_encounter_once_later_runs_append_assessments()
    {
        var first = await CreateAndClaim("first");
        Assert.True(await Repository.CompleteScanAsync(first, Result(Encounter(10))));
        var original = Assert.Single((await Repository.ListAsync(new ConjunctionQuery("Simulation"))).Items);
        var second = await CreateAndClaim("second");
        Assert.True(await Repository.CompleteScanAsync(second, Result(Encounter(11, missDistance: 200))));
        var latest = Assert.Single((await Repository.ListAsync(new ConjunctionQuery("Simulation"))).Items);
        Assert.Equal(original.Id, latest.Id);
        Assert.Equal(second.Run.Id, latest.LatestAssessment.PredictionRunId);
        Assert.Equal(200, latest.LatestAssessment.MissDistanceMeters);
        var history = await Repository.ListAssessmentsAsync(latest.Id);
        Assert.Equal(2, history.TotalCount);
        Assert.Equal(100, history.Items[1].MissDistanceMeters);
        Assert.Equal(1, await CountEvents(ConjunctionIdentifiedEvent.TypeName));
        Assert.Equal(2, await CountEvents(ConjunctionAssessmentCreatedEvent.TypeName));
        Assert.False(await Repository.CompleteScanAsync(second, Result(Encounter(11))));
        Assert.Equal(2, await Count("conjunction_assessments"));
    }

    [Fact]
    public async Task Reversed_pair_is_canonical_and_preserves_corresponding_state_vectors()
    {
        var lease = await CreateAndClaim("reverse");
        var encounter = Encounter(10);
        Assert.True(await Repository.CompleteScanAsync(lease, Result(encounter with
        {
            PrimaryObjectId = SecondaryId, SecondaryObjectId = PrimaryId,
            PrimaryState = encounter.SecondaryState, SecondaryState = encounter.PrimaryState
        })));
        var summary = Assert.Single((await Repository.ListAsync(new ConjunctionQuery("Simulation"))).Items);
        Assert.Equal(PrimaryId, summary.PrimaryObjectId);
        Assert.Equal(encounter.PrimaryState, summary.LatestAssessment.PrimaryState);
        Assert.Equal(encounter.SecondaryState, summary.LatestAssessment.SecondaryState);
    }

    [Fact]
    public async Task Repeated_passes_retain_separate_histories_and_ambiguous_shift_is_not_merged()
    {
        var first = await CreateAndClaim("first");
        Assert.True(await Repository.CompleteScanAsync(first, Result(Encounter(10), Encounter(12))));
        var initial = (await Repository.ListAsync(new ConjunctionQuery("Simulation"))).Items;
        Assert.Equal(2, initial.Count);
        var second = await CreateAndClaim("second");
        Assert.True(await Repository.CompleteScanAsync(second, Result(Encounter(10.1), Encounter(12.1))));
        var updated = (await Repository.ListAsync(new ConjunctionQuery("Simulation"))).Items;
        Assert.Equal(initial.Select(x => x.Id), updated.Select(x => x.Id));
        var third = await CreateAndClaim("third");
        Assert.True(await Repository.CompleteScanAsync(third, Result(Encounter(11.1))));
        var ambiguous = Assert.Single((await Repository.ListAsync(new ConjunctionQuery("Simulation"))).Items);
        Assert.DoesNotContain(ambiguous.Id, initial.Select(x => x.Id));
        Assert.Equal(3, await Count("conjunctions"));
    }

    [Fact]
    public async Task Outbox_failure_rolls_back_assessment_identity_and_completion_then_retry_succeeds()
    {
        var lease = await CreateAndClaim("atomic");
        await ForceOutboxFailure();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => Repository.CompleteScanAsync(lease, Result(Encounter(10))));
            Assert.Equal(0, await Count("conjunctions"));
            Assert.Equal(0, await Count("conjunction_assessments"));
            Assert.Equal("Running", (await Repository.GetRunAsync(lease.Run.Id))!.Status);
        }
        finally { await RemoveOutboxFailure(); }
        Assert.True(await Repository.CompleteScanAsync(lease, Result(Encounter(10))));
        Assert.Equal("Completed", (await Repository.GetRunAsync(lease.Run.Id))!.Status);
    }

    [Fact]
    public async Task Oldest_run_gates_scope_even_when_message_is_delayed_and_other_scope_can_run()
    {
        var first = await Create("first");
        var second = await Create("second");
        await Repository.TryEnqueueScanAsync(await CompletedEvent(second.Id));
        Assert.Null(await Repository.ClaimAsync(TimeSpan.FromMinutes(1)));
        await Repository.TryEnqueueScanAsync(await CompletedEvent(first.Id));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Repository.ClaimAsync(TimeSpan.FromMinutes(1))));
        var owner = Assert.Single(claims, x => x is not null)!;
        Assert.Equal(first.Id, owner.Run.Id);
        var independent = await Repository.CreateSimulationRunAsync(Context() with { ScenarioId = Guid.NewGuid() }, "other-scope");
        await Repository.TryEnqueueScanAsync(await CompletedEvent(independent.Id));
        Assert.Equal(independent.Id, (await Repository.ClaimAsync(TimeSpan.FromMinutes(1)))!.Run.Id);
        Assert.Null(await Repository.ClaimAsync(TimeSpan.FromMinutes(1)));
        Assert.True(await Repository.CompleteScanAsync(owner, Result()));
        Assert.Equal(second.Id, (await Repository.ClaimAsync(TimeSpan.FromMinutes(1)))!.Run.Id);
    }

    [Fact]
    public async Task Expired_lease_rejects_commit_failure_and_renewal_and_replacement_fences_old_owner()
    {
        var stale = await CreateAndClaim("fenced");
        await Sql("UPDATE conjunction_scan_jobs SET lease_expires_at=clock_timestamp()-interval '1 second';");
        Assert.False(await Repository.RenewAsync(stale, TimeSpan.FromMinutes(1)));
        Assert.False(await Repository.CompleteScanAsync(stale, Result(Encounter(10))));
        Assert.False(await Repository.FailAsync(stale, "late failure"));
        var replacement = (await Repository.ClaimAsync(TimeSpan.FromMinutes(1)))!;
        Assert.NotEqual(stale.LeaseToken, replacement.LeaseToken);
        Assert.Equal(2, replacement.Run.Attempts);
        Assert.False(await Repository.CompleteScanAsync(stale, Result(Encounter(10))));
        Assert.True(await Repository.RenewAsync(replacement, TimeSpan.FromMinutes(1)));
        var completions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Repository.CompleteScanAsync(replacement, Result(Encounter(10)))));
        Assert.Single(completions, x => x);
        Assert.Equal(1, await Count("conjunctions"));
        Assert.Equal(1, await Count("conjunction_assessments"));
    }

    [Fact]
    public async Task Repeated_failures_are_bounded_and_failed_run_does_not_claim_successful_empty_scan()
    {
        var lease = await CreateAndClaim("poison");
        for (var attempt = 1; attempt <= ConjunctionRepository.MaxAttempts; attempt++)
        {
            Assert.Equal(attempt, lease.Run.Attempts);
            Assert.True(await Repository.FailAsync(lease, "Propagation failed"));
            if (attempt == ConjunctionRepository.MaxAttempts) break;
            Assert.Null(await Repository.ClaimAsync(TimeSpan.FromMinutes(1)));
            await Sql("UPDATE conjunction_scan_jobs SET next_attempt_at=clock_timestamp()-interval '1 second';");
            lease = (await Repository.ClaimAsync(TimeSpan.FromMinutes(1)))!;
        }
        var failed = (await Repository.GetRunAsync(lease.Run.Id))!;
        Assert.Equal("Failed", failed.Status);
        Assert.Null(failed.CompletedAtUtc);
        Assert.Null(failed.Metrics);
        Assert.Equal("Propagation failed", failed.LastError);
        Assert.Null(await Repository.ClaimAsync(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Lease_heartbeat_can_continue_while_assessment_publication_is_in_progress()
    {
        var lease = await CreateAndClaim("slow-publication");
        await using var gate = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await gate.BeginTransactionAsync();
        await gate.ExecuteAsync("LOCK TABLE conjunctions IN SHARE MODE;", transaction: transaction);
        var publication = Repository.CompleteScanAsync(lease, Result(Encounter(10)));
        try
        {
            var blocked = false;
            for (var attempt = 0; attempt < 100 && !blocked; attempt++)
            {
                blocked = await Scalar<bool>("""
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                        WHERE datname=current_database() AND wait_event_type='Lock'
                        AND query LIKE 'INSERT INTO conjunctions%');
                    """);
                if (!blocked) await Task.Delay(20);
            }
            Assert.True(blocked, "Publication must hold its run lock before checking the heartbeat.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Assert.True(await Repository.RenewAsync(lease, TimeSpan.FromMinutes(1), timeout.Token));
        }
        finally
        {
            await transaction.RollbackAsync();
            await publication;
        }
        Assert.True(await publication);
        Assert.Equal("Completed", (await Repository.GetRunAsync(lease.Run.Id))!.Status);
    }

    [Fact]
    public async Task Default_query_uses_latest_completed_run_and_explicit_history_is_filterable_and_paginated()
    {
        var first = await CreateAndClaim("first");
        Assert.True(await Repository.CompleteScanAsync(first, Result(Encounter(10, 100), Encounter(30, 200))));
        var page = await Repository.ListAsync(new ConjunctionQuery("Simulation", PrimaryId,
            FromUtc: Epoch.AddMinutes(5), ToUtc: Epoch.AddMinutes(35), MaxMissDistanceMeters: 200, PageSize: 1));
        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(100, page.Items[0].LatestAssessment.MissDistanceMeters);
        Assert.Empty((await Repository.ListAsync(new ConjunctionQuery("Real"))).Items);
        Assert.Empty((await Repository.ListAsync(new ConjunctionQuery("Simulation", Guid.NewGuid()))).Items);
        var empty = await CreateAndClaim("empty");
        Assert.True(await Repository.CompleteScanAsync(empty, Result()));
        Assert.Empty((await Repository.ListAsync(new ConjunctionQuery("Simulation"))).Items);
        Assert.Equal(2, (await Repository.ListAsync(new ConjunctionQuery("Simulation", PredictionRunId: first.Run.Id))).TotalCount);
        Assert.Equal(2, (await Repository.ListRunsAsync("Simulation")).TotalCount);
        var detail = (await Repository.GetAsync(page.Items[0].Id))!;
        Assert.Equal(detail.LatestAssessment, await Repository.GetAssessmentAsync(detail.LatestAssessment.Id));
        Assert.Null(await Repository.GetAssessmentAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Duplicate_encounter_results_roll_back_instead_of_creating_two_logical_identities()
    {
        var lease = await CreateAndClaim("duplicates");
        await Assert.ThrowsAsync<ArgumentException>(() => Repository.CompleteScanAsync(lease, Result(Encounter(10), Encounter(10))));
        Assert.Equal(0, await Count("conjunctions"));
        Assert.Equal("Running", (await Repository.GetRunAsync(lease.Run.Id))!.Status);
    }

    [Fact]
    public async Task Permanently_unpublished_run_fails_without_blocking_a_later_delivered_run()
    {
        var lost = await Create("lost-publication");
        await Sql("UPDATE outbox_messages SET dead_lettered_at=clock_timestamp() WHERE payload->>'predictionRunId'=@id;",
            new { id = lost.Id.ToString() });
        var later = await Create("later-delivered");
        await Repository.TryEnqueueScanAsync(await CompletedEvent(later.Id));
        var lease = await Repository.ClaimAsync(TimeSpan.FromMinutes(1));
        Assert.NotNull(lease);
        Assert.Equal(later.Id, lease.Run.Id);
        Assert.Equal("Failed", (await Repository.GetRunAsync(lost.Id))!.Status);
    }

    [Fact]
    public async Task Late_delivery_records_inbox_without_resurrecting_a_terminal_run()
    {
        var lost = await Create("late-publication");
        var message = await CompletedEvent(lost.Id);
        await Sql("UPDATE outbox_messages SET dead_lettered_at=clock_timestamp();");
        Assert.Null(await Repository.ClaimAsync(TimeSpan.FromMinutes(1)));
        Assert.Equal("Failed", (await Repository.GetRunAsync(lost.Id))!.Status);
        Assert.False(await Repository.TryEnqueueScanAsync(message));
        Assert.False(await Repository.TryEnqueueScanAsync(message));
        Assert.Equal(1, await Count("processed_messages"));
        Assert.Equal(0, await Count("conjunction_scan_jobs"));
        Assert.Null(await Repository.ClaimAsync(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Delivered_job_survives_lost_publication_acknowledgement()
    {
        var run = await Create("delivered-before-cleanup");
        await Sql("UPDATE outbox_messages SET dead_lettered_at=clock_timestamp();");
        Assert.True(await Repository.TryEnqueueScanAsync(await CompletedEvent(run.Id)));
        var lease = await Repository.ClaimAsync(TimeSpan.FromMinutes(1));
        Assert.NotNull(lease);
        Assert.Equal(run.Id, lease.Run.Id);
        Assert.Equal("Running", (await Repository.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task Concurrent_cleanup_and_delivery_never_leave_a_failed_run_with_a_new_job()
    {
        for (var iteration = 0; iteration < 12; iteration++)
        {
            await fixture.ResetAsync();
            var run = await Create($"publication-race-{iteration}");
            var message = await CompletedEvent(run.Id);
            await Sql("UPDATE outbox_messages SET dead_lettered_at=clock_timestamp();");
            var delivery = Task.Run(() => Repository.TryEnqueueScanAsync(message));
            var cleanup = Task.Run(() => Repository.ClaimAsync(TimeSpan.FromMinutes(1)));
            await Task.WhenAll(delivery, cleanup);
            var status = (await Repository.GetRunAsync(run.Id))!.Status;
            var jobs = await Count("conjunction_scan_jobs");
            Assert.True(status == "Failed" && jobs == 0 || status is "Pending" or "Running" && jobs == 1,
                $"Unexpected terminal/job combination: {status}, {jobs} jobs.");
            Assert.Equal(1, await Count("processed_messages"));
        }
    }

    [Fact]
    public async Task Concurrent_claimers_cannot_skip_the_oldest_leased_run_to_claim_later_work()
    {
        var queued = new List<PredictionRunRecord>();
        for (var i = 0; i < 12; i++)
        {
            var run = await Create($"ordered-{i}");
            queued.Add(run);
            await Repository.TryEnqueueScanAsync(await CompletedEvent(run.Id));
        }
        foreach (var expected in queued)
        {
            var claims = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(_ => Task.Run(() => Repository.ClaimAsync(TimeSpan.FromMinutes(1)))));
            var owner = Assert.Single(claims, x => x is not null)!;
            Assert.Equal(expected.Id, owner.Run.Id);
            Assert.True(await Repository.CompleteScanAsync(owner, Result()));
        }
    }

    private Task<PredictionRunRecord> Create(string key) => Repository.CreateSimulationRunAsync(Context(), key);
    private async Task<ConjunctionScanLease> CreateAndClaim(string key)
    {
        var run = await Create(key);
        await Repository.TryEnqueueScanAsync(await CompletedEvent(run.Id));
        return (await Repository.ClaimAsync(TimeSpan.FromMinutes(1)))!;
    }
    private async Task<PredictionRunCompletedEvent> CompletedEvent(Guid id) => JsonSerializer.Deserialize<PredictionRunCompletedEvent>(
        await Scalar<string>("SELECT payload::text FROM outbox_messages WHERE event_type=@type AND payload->>'predictionRunId'=@id;",
            new { type = PredictionRunCompletedEvent.TypeName, id = id.ToString("D") }), Serialization.Options)!;
    private static PredictionRunContext Context() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, Epoch, Epoch.AddHours(24), 60,
        "Simulation", ScenarioId, "test-analytic-v1", "test-analytic-v1", new ConjunctionSettings(),
        [Snapshot(PrimaryId, "Synthetic A"), Snapshot(SecondaryId, "Synthetic B")]);
    private static OrbitSnapshot Snapshot(Guid id, string name)
    {
        var elements = Elements();
        return new OrbitSnapshot(new OrbitalObject(id, null, name, null, "Simulation", Epoch), 1,
            elements, elements.ComputeFingerprint(), Epoch);
    }
    private static CatalogImport Import(Guid id, long norad) => new(id, norad, $"Object {norad}", null, "Unknown", Elements());
    private static OrbitalElements Elements() => new(Epoch, 15.5, 0.001, 51.6, 20, 30, 40, 0, 0, 0);
    private static RefinedConjunction Encounter(double minutes, double missDistance = 100)
    {
        var time = Epoch.AddMinutes(minutes);
        return new RefinedConjunction(PrimaryId, SecondaryId, time, missDistance, 14000,
            new TemeStateSi(time, new CartesianVector(7000000, 0, 0), new CartesianVector(0, 7000, 0)),
            new TemeStateSi(time, new CartesianVector(7000000, 0, missDistance), new CartesianVector(0, -7000, 0)), false);
    }
    private static ConjunctionScanResult Result(params RefinedConjunction[] encounters) => new(encounters,
        new ConjunctionScanMetrics(2, 1, 1440, encounters.Length, encounters.Length, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10)));
    private Task ForceOutboxFailure() => Sql("ALTER TABLE outbox_messages ADD CONSTRAINT forced_failure CHECK (false) NOT VALID;");
    private Task RemoveOutboxFailure() => Sql("ALTER TABLE outbox_messages DROP CONSTRAINT forced_failure;");
    private Task<int> Count(string table) => Scalar<int>($"SELECT count(*) FROM {table};");
    private Task<int> CountEvents(string type) => Scalar<int>("SELECT count(*) FROM outbox_messages WHERE event_type=@type;", new { type });
    private async Task Sql(string sql, object? parameters = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
    private async Task<T> Scalar<T>(string sql, object? parameters = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }
}
