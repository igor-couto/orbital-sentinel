using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.Infrastructure;

/// <summary>Durable input manifests, fenced scan jobs and immutable encounter assessments.</summary>
public sealed class ConjunctionRepository(NpgsqlDataSource dataSource, ILogger<ConjunctionRepository>? logger = null)
{
    public const int MaxAttempts = 5;
    private const string RunColumns = """
        r.manifest::text AS Manifest,r.status AS Status,r.attempts AS Attempts,
        r.last_error AS LastError,r.completed_at AS CompletedAt,r.metrics::text AS Metrics
        """;

    public async Task<PredictionRunRecord?> CreateRealRunAsync(DateTimeOffset startUtc, TimeSpan horizon,
        TimeSpan step, ConjunctionSettings settings, string requestKey, CancellationToken cancellationToken = default)
    {
        ValidateRequestKey(requestKey);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockRequest(connection, transaction, requestKey, cancellationToken);
        var existing = await FindRequest(connection, transaction, requestKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Context.Source != "Real")
                throw new ArgumentException("The request key belongs to a simulation.", nameof(requestKey));
            return existing;
        }
        await LockScope(connection, transaction, "Real", cancellationToken);
        // One statement observes a coherent MVCC snapshot, including names, versions and full input elements.
        var rows = (await connection.QueryAsync<SnapshotRow>(new CommandDefinition("""
            SELECT o.id AS Id,o.norad_catalog_id AS NoradCatalogId,o.name AS Name,
                o.international_designator AS InternationalDesignator,o.object_type AS ObjectType,
                o.updated_at AS UpdatedAt,e.version AS Version,e.elements::text AS Elements,
                e.fingerprint AS Fingerprint,e.ingested_at AS IngestedAt
            FROM orbital_objects o JOIN orbital_element_sets e
                ON e.orbital_object_id=o.id AND e.version=o.current_version ORDER BY o.id LIMIT 201;
            """, transaction: transaction, cancellationToken: cancellationToken))).ToArray();
        if (rows.Length < 2) return null;
        if (rows.Length > 200)
            throw new InvalidOperationException("Conjunction screening currently supports at most 200 catalog objects; the run was not silently truncated.");
        if (step.Ticks % TimeSpan.TicksPerSecond != 0 || step.TotalSeconds is < 1 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(step));
        var context = new PredictionRunContext(Guid.NewGuid(), DateTimeOffset.UtcNow, startUtc.ToUniversalTime(),
            startUtc.Add(horizon).ToUniversalTime(), (int)step.TotalSeconds, "Real", null,
            ConjunctionAlgorithms.Current, ConjunctionAlgorithms.PropagatorVersion, settings, rows.Select(x => x.ToSnapshot()).ToArray());
        var run = await InsertRun(connection, transaction, context, requestKey, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    public async Task<PredictionRunRecord> CreateSimulationRunAsync(PredictionRunContext context, string requestKey,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestKey(requestKey);
        if (context.Source != "Simulation" || context.ScenarioId is null || context.Objects.Count != 2)
            throw new ArgumentException("A simulation requires a scenario identity and exactly two synthetic objects.", nameof(context));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockRequest(connection, transaction, requestKey, cancellationToken);
        var existing = await FindRequest(connection, transaction, requestKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Context.Source != context.Source || existing.Context.ScenarioId != context.ScenarioId)
                throw new ArgumentException("The request key belongs to another prediction scope.", nameof(requestKey));
            return existing;
        }
        var run = await InsertRun(connection, transaction, context, requestKey, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    private static async Task<PredictionRunRecord> InsertRun(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PredictionRunContext context, string requestKey, CancellationToken cancellationToken)
    {
        ValidateContext(context);
        var scope = Scope(context);
        // Hold the scope row through sequence allocation and commit. Otherwise an uncommitted earlier
        // run could appear after a later run has already been claimed and assessed by another worker.
        await LockScope(connection, transaction, scope, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO prediction_runs(id,request_key,scope_key,source,scenario_id,created_at,prediction_start,prediction_end,manifest)
            VALUES (@Id,@requestKey,@scope,@Source,@ScenarioId,@CreatedAtUtc,@PredictionStartUtc,@PredictionEndUtc,CAST(@manifest AS jsonb));
            """, new { context.Id, requestKey, scope, context.Source, context.ScenarioId,
                context.CreatedAtUtc, context.PredictionStartUtc, context.PredictionEndUtc,
                manifest = Serialize(context) }, transaction, cancellationToken: cancellationToken));
        var message = new PredictionRunCompletedEvent(Guid.NewGuid(), PredictionRunCompletedEvent.TypeName,
            DateTimeOffset.UtcNow, context.Id, 1, context.Id, context.Source, context.ScenarioId);
        await Append(connection, transaction, message.EventId, message.EventType, context.Id,
            message, message.OccurredAt, cancellationToken);
        return new PredictionRunRecord(context, "Pending", 0, null, null, null);
    }

    public async Task<bool> TryEnqueueScanAsync(PredictionRunCompletedEvent message,
        CancellationToken cancellationToken = default)
    {
        if (message.EventId == Guid.Empty || message.PredictionRunId == Guid.Empty ||
            message.EventType != PredictionRunCompletedEvent.TypeName || message.SchemaVersion != 1)
            throw new ArgumentException("Invalid prediction-run event.", nameof(message));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var scope = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT scope_key FROM prediction_runs WHERE id=@PredictionRunId AND source=@Source
                AND scenario_id IS NOT DISTINCT FROM @ScenarioId;
            """, message, transaction, cancellationToken: cancellationToken));
        if (scope is null) throw new ArgumentException("The event does not identify a sealed prediction run.", nameof(message));
        await LockScope(connection, transaction, scope, cancellationToken);
        // Publication cleanup and delivery share the scope lock. A terminal run stays terminal:
        // resurrecting it after later runs were assessed would reorder the encounter history.
        var accepting = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT status IN ('Pending','Running') FROM prediction_runs WHERE id=@PredictionRunId;
            """, message, transaction, cancellationToken: cancellationToken));
        var inserted = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO processed_messages(consumer_name,event_id) VALUES ('conjunction-scheduling',@EventId)
            ON CONFLICT DO NOTHING;
            """, message, transaction, cancellationToken: cancellationToken));
        var scheduled = inserted == 1 && accepting && await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO conjunction_scan_jobs(prediction_run_id,event_id) VALUES (@PredictionRunId,@EventId)
            ON CONFLICT (prediction_run_id) DO NOTHING;
            """, message, transaction, cancellationToken: cancellationToken)) == 1;
        await transaction.CommitAsync(cancellationToken);
        return scheduled;
    }

    public async Task<ConjunctionScanLease?> ClaimAsync(TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(leaseDuration);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // A permanently unpublished run must not block every later run in the same scope.
        // Take the same lock as delivery, then recheck in a fresh statement so a committed job wins.
        var unpublishedScopes = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT s.scope_key FROM conjunction_scopes s WHERE EXISTS (
                SELECT 1 FROM prediction_runs r WHERE r.scope_key=s.scope_key AND r.status='Pending'
                    AND NOT EXISTS(SELECT 1 FROM conjunction_scan_jobs j WHERE j.prediction_run_id=r.id)
                    AND EXISTS(SELECT 1 FROM outbox_messages m WHERE m.event_type=@type
                        AND m.payload->>'predictionRunId'=r.id::text AND m.dead_lettered_at IS NOT NULL))
            ORDER BY s.scope_key FOR UPDATE OF s SKIP LOCKED;
            """, new { type = PredictionRunCompletedEvent.TypeName }, transaction, cancellationToken: cancellationToken))).ToArray();
        foreach (var unpublishedScope in unpublishedScopes)
            await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE prediction_runs r SET status='Failed',last_error='Prediction-run publication exhausted its retries.'
            WHERE r.scope_key=@scope AND r.status='Pending'
                AND NOT EXISTS(SELECT 1 FROM conjunction_scan_jobs j WHERE j.prediction_run_id=r.id)
                AND EXISTS(SELECT 1 FROM outbox_messages m WHERE m.event_type=@type
                    AND m.payload->>'predictionRunId'=r.id::text AND m.dead_lettered_at IS NOT NULL);
            """, new { scope = unpublishedScope, type = PredictionRunCompletedEvent.TypeName }, transaction,
                cancellationToken: cancellationToken));
        // A persistent scope row serializes claimers. The oldest unfinished run gates later runs even
        // when its Kafka message is delayed, ensuring repeatable history correlation across replicas.
        var scope = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT s.scope_key FROM conjunction_scopes s
            JOIN LATERAL (SELECT r.id FROM prediction_runs r WHERE r.scope_key=s.scope_key
                AND r.status IN ('Pending','Running') ORDER BY r.sequence LIMIT 1) oldest ON true
            JOIN conjunction_scan_jobs j ON j.prediction_run_id=oldest.id
            WHERE j.next_attempt_at<=clock_timestamp() AND (j.lease_token IS NULL OR j.lease_expires_at<=clock_timestamp())
            ORDER BY s.scope_key FOR UPDATE OF s SKIP LOCKED LIMIT 1;
            """, transaction: transaction, cancellationToken: cancellationToken));
        if (scope is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition($"""
            SELECT {RunColumns} FROM prediction_runs r JOIN conjunction_scan_jobs j ON j.prediction_run_id=r.id
            WHERE r.scope_key=@scope AND r.status IN ('Pending','Running')
                AND r.id=(SELECT oldest.id FROM prediction_runs oldest WHERE oldest.scope_key=@scope
                    AND oldest.status IN ('Pending','Running') ORDER BY oldest.sequence LIMIT 1)
                AND j.next_attempt_at<=clock_timestamp() AND (j.lease_token IS NULL OR j.lease_expires_at<=clock_timestamp())
            ORDER BY r.sequence LIMIT 1 FOR UPDATE OF r,j;
            """, new { scope }, transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var run = row.ToRecord();
        if (run.Attempts >= MaxAttempts)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE prediction_runs SET status='Failed',last_error='Scan lease expired after the final attempt.' WHERE id=@id;
                UPDATE conjunction_scan_jobs SET lease_token=NULL,lease_expires_at=NULL WHERE prediction_run_id=@id;
                """, new { id = run.Id }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var token = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE conjunction_scan_jobs SET lease_token=@token,lease_expires_at=clock_timestamp()+@leaseDuration
                WHERE prediction_run_id=@id;
            UPDATE prediction_runs SET status='Running',attempts=attempts+1 WHERE id=@id;
            """, new { id = run.Id, token, leaseDuration }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new ConjunctionScanLease(run with { Status = "Running", Attempts = run.Attempts + 1 }, token);
    }

    public async Task<bool> RenewAsync(ConjunctionScanLease lease, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(leaseDuration);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE conjunction_scan_jobs SET lease_expires_at=clock_timestamp()+@leaseDuration
            WHERE prediction_run_id=@id AND lease_token=@LeaseToken AND lease_expires_at>clock_timestamp();
            """, new { id = lease.Run.Id, lease.LeaseToken, leaseDuration }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> FailAsync(ConjunctionScanLease lease, string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await LockOwnedRun(connection, transaction, lease, cancellationToken);
        if (run is null) return false;
        var attempts = run.Attempts;
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE prediction_runs SET status=@status,last_error=@error WHERE id=@id;
            UPDATE conjunction_scan_jobs SET lease_token=NULL,lease_expires_at=NULL,next_attempt_at=clock_timestamp()+@backoff
                WHERE prediction_run_id=@id;
            """, new { id = run.Id, status = attempts >= MaxAttempts ? "Failed" : "Pending",
                error = error[..Math.Min(error.Length, 4000)], backoff = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, attempts))) },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteScanAsync(ConjunctionScanLease lease, ConjunctionScanResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await LockOwnedRun(connection, transaction, lease, cancellationToken);
        if (run is null) return false;
        var context = run.Context;
        var objects = context.Objects.ToDictionary(x => x.Object.Id);
        var scope = Scope(context);
        var detectedAt = DateTimeOffset.UtcNow;
        var canonical = result.Encounters.Select(Canonicalize).ToArray();
        var published = new List<(ConjunctionAssessmentCreatedEvent Event, long? PrimaryNoradId, long? SecondaryNoradId)>();
        foreach (var encounter in canonical) ValidateEncounter(encounter, context, objects);
        foreach (var group in canonical.GroupBy(x => (x.PrimaryObjectId, x.SecondaryObjectId)))
        {
            var encounters = group.OrderBy(x => x.TimeOfClosestApproachUtc).ToArray();
            if (encounters.Zip(encounters.Skip(1)).Any(x => x.First.TimeOfClosestApproachUtc == x.Second.TimeOfClosestApproachUtc))
                throw new ArgumentException("The scan contains duplicate encounters for the same pair and instant.", nameof(result));
            var previous = (await connection.QueryAsync<PreviousEncounter>(new CommandDefinition("""
                SELECT c.id AS Id,a.tca_utc AS TcaUtc FROM conjunctions c
                JOIN conjunction_assessments a ON a.id=c.latest_assessment_id
                WHERE c.scope_key=@scope AND c.primary_object_id=@PrimaryObjectId AND c.secondary_object_id=@SecondaryObjectId
                ORDER BY a.tca_utc,c.id;
                """, new { scope, group.Key.PrimaryObjectId, group.Key.SecondaryObjectId }, transaction,
                cancellationToken: cancellationToken))).ToArray();
            var associations = Associate(encounters, previous, context.Settings.AssociationWindowSeconds);
            for (var ordinal = 0; ordinal < encounters.Length; ordinal++)
            {
                var encounter = encounters[ordinal];
                var primary = objects[encounter.PrimaryObjectId];
                var secondary = objects[encounter.SecondaryObjectId];
                var id = associations[ordinal] ?? Guid.NewGuid();
                if (associations[ordinal] is null)
                {
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO conjunctions(id,scope_key,source,scenario_id,primary_object_id,secondary_object_id,
                            primary_name,secondary_name,primary_norad_id,secondary_norad_id,first_identified_at)
                        VALUES (@id,@scope,@Source,@ScenarioId,@PrimaryObjectId,@SecondaryObjectId,
                            @primaryName,@secondaryName,@primaryNoradId,@secondaryNoradId,@detectedAt);
                        """, new { id, scope, context.Source, context.ScenarioId, encounter.PrimaryObjectId, encounter.SecondaryObjectId,
                            primaryName = primary.Object.Name, secondaryName = secondary.Object.Name,
                            primaryNoradId = primary.Object.NoradCatalogId, secondaryNoradId = secondary.Object.NoradCatalogId, detectedAt },
                        transaction, cancellationToken: cancellationToken));
                    var identified = new ConjunctionIdentifiedEvent(Guid.NewGuid(), ConjunctionIdentifiedEvent.TypeName,
                        detectedAt, context.Id, 1, id, context.Id, encounter.PrimaryObjectId, encounter.SecondaryObjectId,
                        context.Source, context.ScenarioId);
                    await Append(connection, transaction, identified.EventId, identified.EventType, id,
                        identified, detectedAt, cancellationToken);
                }
                var assessment = new ConjunctionAssessmentRecord(Guid.NewGuid(), id, context.Id, detectedAt,
                    encounter.TimeOfClosestApproachUtc, encounter.MissDistanceMeters, encounter.RelativeVelocityMetersPerSecond,
                    encounter.PrimaryState, encounter.SecondaryState, primary.Version, secondary.Version,
                    encounter.IsWindowBoundary, encounter.IsExtendedProximity);
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO conjunction_assessments(id,conjunction_id,prediction_run_id,primary_object_id,secondary_object_id,
                        encounter_ordinal,detected_at,tca_utc,miss_distance_meters,relative_velocity_meters_per_second,payload)
                    VALUES (@Id,@ConjunctionId,@PredictionRunId,@primaryId,@secondaryId,@ordinal,@DetectedAtUtc,
                        @TimeOfClosestApproachUtc,@MissDistanceMeters,@RelativeVelocityMetersPerSecond,CAST(@payload AS jsonb));
                    UPDATE conjunctions SET latest_assessment_id=@Id,primary_name=@primaryName,secondary_name=@secondaryName
                        WHERE id=@ConjunctionId;
                    """, new { assessment.Id, assessment.ConjunctionId, assessment.PredictionRunId,
                        primaryId = encounter.PrimaryObjectId, secondaryId = encounter.SecondaryObjectId, ordinal,
                        assessment.DetectedAtUtc, assessment.TimeOfClosestApproachUtc, assessment.MissDistanceMeters,
                        assessment.RelativeVelocityMetersPerSecond, payload = Serialize(assessment),
                        primaryName = primary.Object.Name, secondaryName = secondary.Object.Name },
                    transaction, cancellationToken: cancellationToken));
                var created = new ConjunctionAssessmentCreatedEvent(Guid.NewGuid(), ConjunctionAssessmentCreatedEvent.TypeName,
                    detectedAt, context.Id, 1, id, assessment.Id, context.Id, encounter.PrimaryObjectId,
                    encounter.SecondaryObjectId, context.Source, context.ScenarioId, encounter.TimeOfClosestApproachUtc,
                    encounter.MissDistanceMeters, encounter.RelativeVelocityMetersPerSecond);
                await Append(connection, transaction, created.EventId, created.EventType, id, created, detectedAt, cancellationToken);
                published.Add((created, primary.Object.NoradCatalogId, secondary.Object.NoradCatalogId));
            }
        }
        var fenced = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE conjunction_scan_jobs SET lease_token=NULL,lease_expires_at=NULL
            WHERE prediction_run_id=@id AND lease_token=@LeaseToken AND lease_expires_at>clock_timestamp();
            """, new { id = context.Id, lease.LeaseToken }, transaction, cancellationToken: cancellationToken));
        if (fenced != 1) return false; // Disposing the transaction rolls every scientific record/event back.
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE prediction_runs SET status='Completed',completed_at=@detectedAt,last_error=NULL,metrics=CAST(@metrics AS jsonb) WHERE id=@id;
            """, new { id = context.Id, detectedAt, metrics = Serialize(result.Metrics) }, transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        foreach (var entry in published)
            logger?.LogInformation("Conjunction assessment committed: {ConjunctionId} {AssessmentId} run {PredictionRunId}, pair {PrimaryObjectId}/{SecondaryObjectId}, NORAD {PrimaryNoradId}/{SecondaryNoradId}, event {EventId}, TCA {TCA}, miss {MissDistanceMeters} m",
                entry.Event.ConjunctionId, entry.Event.AssessmentId, entry.Event.PredictionRunId,
                entry.Event.PrimaryObjectId, entry.Event.SecondaryObjectId, entry.PrimaryNoradId, entry.SecondaryNoradId,
                entry.Event.EventId, entry.Event.TimeOfClosestApproachUtc, entry.Event.MissDistanceMeters);
        return true;
    }

    public async Task<PredictionRunRecord?> GetRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM prediction_runs r WHERE r.id=@id;", new { id }, cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    public async Task<Page<PredictionRunRecord>> ListRunsAsync(string? source = null, int page = 1, int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize);
        ValidateSource(source);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var parameters = new { source, pageSize, offset = (page - 1) * pageSize };
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM prediction_runs WHERE @source IS NULL OR source=@source;", parameters, transaction,
            cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition($"""
            SELECT {RunColumns} FROM prediction_runs r WHERE @source IS NULL OR r.source=@source
            ORDER BY r.sequence DESC LIMIT @pageSize OFFSET @offset;
            """, parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new Page<PredictionRunRecord>(rows.Select(x => x.ToRecord()).ToArray(), page, pageSize, count);
    }

    private const string SummaryColumns = """
        c.id AS Id,c.source AS Source,c.scenario_id AS ScenarioId,c.primary_object_id AS PrimaryObjectId,
        c.secondary_object_id AS SecondaryObjectId,c.primary_name AS PrimaryName,c.secondary_name AS SecondaryName,
        c.primary_norad_id AS PrimaryNoradId,c.secondary_norad_id AS SecondaryNoradId,
        c.first_identified_at AS FirstIdentifiedAt,a.payload::text AS Assessment
        """;

    public async Task<Page<ConjunctionSummary>> ListAsync(ConjunctionQuery query, CancellationToken cancellationToken = default)
    {
        ValidatePage(query.Page, query.PageSize);
        ValidateSource(query.Source);
        if (query.FromUtc > query.ToUtc || query.MaxMissDistanceMeters is { } distance && (!double.IsFinite(distance) || distance < 0))
            throw new ArgumentException("Invalid TCA range or maximum distance.", nameof(query));
        const string filter = """
            FROM conjunctions c JOIN conjunction_assessments a ON a.conjunction_id=c.id
            JOIN prediction_runs r ON r.id=a.prediction_run_id
            WHERE (@source IS NULL OR c.source=@source)
                AND (@objectId IS NULL OR c.primary_object_id=@objectId OR c.secondary_object_id=@objectId)
                AND (@runId IS NULL OR a.prediction_run_id=@runId)
                AND (@fromUtc IS NULL OR a.tca_utc>=@fromUtc) AND (@toUtc IS NULL OR a.tca_utc<=@toUtc)
                AND (@maxDistance IS NULL OR a.miss_distance_meters<=@maxDistance)
                AND (@runId IS NOT NULL OR (
                    (@latestRunOnly AND r.id=(SELECT latest.id FROM prediction_runs latest
                        WHERE latest.scope_key=c.scope_key AND latest.status='Completed' ORDER BY latest.sequence DESC LIMIT 1))
                    OR (NOT @latestRunOnly AND a.id=c.latest_assessment_id)))
            """;
        var parameters = new { source = query.Source, objectId = query.ObjectId, runId = query.PredictionRunId,
            fromUtc = query.FromUtc?.ToUniversalTime(), toUtc = query.ToUtc?.ToUniversalTime(), maxDistance = query.MaxMissDistanceMeters,
            latestRunOnly = query.LatestRunOnly, pageSize = query.PageSize, offset = (query.Page - 1) * query.PageSize };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) " + filter,
            parameters, transaction, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition($"SELECT {SummaryColumns} " + filter +
            " ORDER BY a.tca_utc,c.id LIMIT @pageSize OFFSET @offset;", parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new Page<ConjunctionSummary>(rows.Select(x => x.ToSummary()).ToArray(), query.Page, query.PageSize, count);
    }

    public async Task<ConjunctionSummary?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SummaryRow>(new CommandDefinition($"""
            SELECT {SummaryColumns} FROM conjunctions c JOIN conjunction_assessments a ON a.id=c.latest_assessment_id
            WHERE c.id=@id;
            """, new { id }, cancellationToken: cancellationToken));
        return row?.ToSummary();
    }

    public async Task<Page<ConjunctionAssessmentRecord>> ListAssessmentsAsync(Guid id, int page = 1, int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var parameters = new { id, pageSize, offset = (page - 1) * pageSize };
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM conjunction_assessments WHERE conjunction_id=@id;", parameters, transaction,
            cancellationToken: cancellationToken));
        var payloads = await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT a.payload::text FROM conjunction_assessments a JOIN prediction_runs r ON r.id=a.prediction_run_id
            WHERE a.conjunction_id=@id ORDER BY r.sequence DESC LIMIT @pageSize OFFSET @offset;
            """, parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new Page<ConjunctionAssessmentRecord>(payloads.Select(Deserialize<ConjunctionAssessmentRecord>).ToArray(), page, pageSize, count);
    }

    public async Task<ConjunctionAssessmentRecord?> GetAssessmentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload::text FROM conjunction_assessments WHERE id=@id;", new { id }, cancellationToken: cancellationToken));
        return payload is null ? null : Deserialize<ConjunctionAssessmentRecord>(payload);
    }

    // Match only unambiguous, mutually unique associations. Half the nearest encounter gap narrows
    // each side's window, preventing a pair's nearby passes from collapsing into one history.
    private static Guid?[] Associate(RefinedConjunction[] current, PreviousEncounter[] previous, double windowSeconds)
    {
        var edges = new List<(int Current, int Previous)>();
        var currentTimes = current.Select(x => x.TimeOfClosestApproachUtc).ToArray();
        var previousTimes = previous.Select(x => new DateTimeOffset(x.TcaUtc)).ToArray();
        for (var i = 0; i < current.Length; i++)
        for (var j = 0; j < previous.Length; j++)
        {
            var window = Math.Min(windowSeconds, Math.Min(Gap(currentTimes, i), Gap(previousTimes, j)) / 2);
            // PostgreSQL timestamps retain microseconds; a round-trip must not break a symmetric tie.
            if (Math.Abs((currentTimes[i] - previousTimes[j]).TotalSeconds) <= window + 0.000001)
                edges.Add((i, j));
        }
        var matches = new Guid?[current.Length];
        foreach (var edge in edges)
            if (edges.Count(x => x.Current == edge.Current) == 1 && edges.Count(x => x.Previous == edge.Previous) == 1)
                matches[edge.Current] = previous[edge.Previous].Id;
        return matches;
    }

    private static double Gap(DateTimeOffset[] times, int index) => Math.Min(
        index > 0 ? (times[index] - times[index - 1]).TotalSeconds : double.PositiveInfinity,
        index + 1 < times.Length ? (times[index + 1] - times[index]).TotalSeconds : double.PositiveInfinity);

    private static RefinedConjunction Canonicalize(RefinedConjunction encounter) =>
        encounter.PrimaryObjectId.CompareTo(encounter.SecondaryObjectId) <= 0 ? encounter : encounter with
        {
            PrimaryObjectId = encounter.SecondaryObjectId, SecondaryObjectId = encounter.PrimaryObjectId,
            PrimaryState = encounter.SecondaryState, SecondaryState = encounter.PrimaryState
        };

    private static void ValidateEncounter(RefinedConjunction encounter, PredictionRunContext context,
        IReadOnlyDictionary<Guid, OrbitSnapshot> objects)
    {
        if (encounter.PrimaryObjectId == encounter.SecondaryObjectId || !objects.ContainsKey(encounter.PrimaryObjectId) ||
            !objects.ContainsKey(encounter.SecondaryObjectId) || encounter.TimeOfClosestApproachUtc < context.PredictionStartUtc ||
            encounter.TimeOfClosestApproachUtc > context.PredictionEndUtc || !double.IsFinite(encounter.MissDistanceMeters) ||
            encounter.MissDistanceMeters < 0 || encounter.MissDistanceMeters > context.Settings.ReportingDistanceMeters ||
            !double.IsFinite(encounter.RelativeVelocityMetersPerSecond) || encounter.RelativeVelocityMetersPerSecond < 0 ||
            encounter.PrimaryState.TimestampUtc != encounter.TimeOfClosestApproachUtc ||
            encounter.SecondaryState.TimestampUtc != encounter.TimeOfClosestApproachUtc)
            throw new ArgumentException("Encounter identity, bounds, states or SI quantities do not match the prediction run.");
    }

    private static async Task<PredictionRunRecord?> LockOwnedRun(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ConjunctionScanLease lease, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition($"""
            SELECT {RunColumns} FROM prediction_runs r JOIN conjunction_scan_jobs j ON j.prediction_run_id=r.id
            WHERE r.id=@id AND r.status='Running' AND j.lease_token=@LeaseToken AND j.lease_expires_at>clock_timestamp()
            FOR UPDATE OF r,j;
            """, new { id = lease.Run.Id, lease.LeaseToken }, transaction, cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    private static async Task<PredictionRunRecord?> FindRequest(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string requestKey, CancellationToken cancellationToken) =>
        (await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM prediction_runs r WHERE r.request_key=@requestKey;", new { requestKey },
            transaction, cancellationToken: cancellationToken)))?.ToRecord();

    private static Task LockRequest(NpgsqlConnection connection, NpgsqlTransaction transaction, string requestKey,
        CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition(
        "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", new { key = "prediction-run:" + requestKey },
        transaction, cancellationToken: cancellationToken));

    private static Task LockScope(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope,
        CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition("""
        INSERT INTO conjunction_scopes(scope_key) VALUES (@scope) ON CONFLICT DO NOTHING;
        SELECT scope_key FROM conjunction_scopes WHERE scope_key=@scope FOR UPDATE;
        """, new { scope }, transaction, cancellationToken: cancellationToken));

    private static Task Append<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid eventId,
        string eventType, Guid partitionKey, T message, DateTimeOffset occurredAt, CancellationToken cancellationToken) =>
        OutboxWriter.AppendAsync(connection, transaction, eventId, eventType, null, null, partitionKey.ToString("D"),
            Serialize(message), occurredAt, cancellationToken);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Serialization.Options);
    private static T Deserialize<T>(string payload) => JsonSerializer.Deserialize<T>(payload, Serialization.Options)
        ?? throw new InvalidOperationException("A conjunction record is missing its payload.");
    private static string Scope(PredictionRunContext context) => context.Source == "Real" ? "Real" : $"Simulation:{context.ScenarioId:D}";
    private static void ValidateRequestKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new ArgumentException("A request key of 1..200 characters is required.", nameof(key));
    }
    private static void ValidatePage(int page, int pageSize)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
    }
    private static void ValidateSource(string? source)
    {
        if (source is not null and not "Real" and not "Simulation") throw new ArgumentException("Source must be Real or Simulation.", nameof(source));
    }
    private static void ValidateLease(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(duration));
    }
    private static void ValidateContext(PredictionRunContext context)
    {
        context.Settings.Validate();
        ValidateSource(context.Source);
        if (context.Id == Guid.Empty || context.CreatedAtUtc == default || context.PredictionStartUtc == default ||
            context.PredictionEndUtc <= context.PredictionStartUtc ||
            context.PredictionEndUtc - context.PredictionStartUtc > TimeSpan.FromHours(48) || context.StepSeconds is < 1 or > 300 ||
            Math.Ceiling((context.PredictionEndUtc - context.PredictionStartUtc).TotalSeconds / context.StepSeconds) > 10000 ||
            context.Objects.Count is < 2 or > 200 || context.Objects.Select(x => x.Object.Id).Distinct().Count() != context.Objects.Count ||
            (context.Source == "Real" && context.ScenarioId is not null) || (context.Source == "Simulation" && context.ScenarioId is null))
            throw new ArgumentException("Invalid prediction-run context.", nameof(context));
        foreach (var orbit in context.Objects)
        {
            orbit.Elements.Validate();
            if (orbit.Object.Id == Guid.Empty || orbit.Version < 1 || orbit.Fingerprint != orbit.Elements.ComputeFingerprint())
                throw new ArgumentException("A run requires valid, fingerprinted orbital snapshots.", nameof(context));
        }
    }

    private sealed class RunRow
    {
        public string Manifest { get; init; } = "";
        public string Status { get; init; } = "";
        public int Attempts { get; init; }
        public string? LastError { get; init; }
        public DateTime? CompletedAt { get; init; }
        public string? Metrics { get; init; }
        public PredictionRunRecord ToRecord() => new(Deserialize<PredictionRunContext>(Manifest), Status, Attempts,
            LastError, CompletedAt, Metrics is null ? null : Deserialize<ConjunctionScanMetrics>(Metrics));
    }
    private sealed record PreviousEncounter(Guid Id, DateTime TcaUtc);
    private sealed class SummaryRow
    {
        public Guid Id { get; init; }
        public string Source { get; init; } = "";
        public Guid? ScenarioId { get; init; }
        public Guid PrimaryObjectId { get; init; }
        public Guid SecondaryObjectId { get; init; }
        public string PrimaryName { get; init; } = "";
        public string SecondaryName { get; init; } = "";
        public long? PrimaryNoradId { get; init; }
        public long? SecondaryNoradId { get; init; }
        public DateTime FirstIdentifiedAt { get; init; }
        public string Assessment { get; init; } = "";
        public ConjunctionSummary ToSummary() => new(Id, Source, ScenarioId, PrimaryObjectId, SecondaryObjectId,
            PrimaryName, SecondaryName, PrimaryNoradId, SecondaryNoradId, FirstIdentifiedAt,
            Deserialize<ConjunctionAssessmentRecord>(Assessment));
    }
    private sealed class SnapshotRow
    {
        public Guid Id { get; init; }
        public long? NoradCatalogId { get; init; }
        public string Name { get; init; } = "";
        public string? InternationalDesignator { get; init; }
        public string ObjectType { get; init; } = "";
        public DateTime UpdatedAt { get; init; }
        public long Version { get; init; }
        public string Elements { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public DateTime IngestedAt { get; init; }
        public OrbitSnapshot ToSnapshot() => new(new OrbitalObject(Id, NoradCatalogId, Name, InternationalDesignator,
            ObjectType, UpdatedAt), Version, Deserialize<OrbitalElements>(Elements), Fingerprint, IngestedAt);
    }
}
