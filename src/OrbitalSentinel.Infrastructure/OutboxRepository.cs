using Dapper;
using Npgsql;

namespace OrbitalSentinel.Infrastructure;

public sealed class OutboxRepository(NpgsqlDataSource dataSource, bool publishTrajectories = true)
{
    public const int MaxAttempts = 8;
    // Preserve queued optional work while disabled, without leasing it or consuming retry attempts.
    private readonly string[] excludedEventTypes = publishTrajectories ? [] :
        [OrbitUpdatedEvent.TypeName, TrajectoryRefreshRequestedEvent.TypeName, TrajectoryUpdatedEvent.TypeName];

    public async Task<IReadOnlyList<OutboxLease>> ClaimAsync(int batchSize, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 200 || leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be 1..200 and lease duration 1s..5min.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Expired leases count as attempts. A crashing publisher cannot monopolize a partition forever.
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE outbox_messages SET dead_lettered_at=clock_timestamp(), lease_token=NULL, lease_expires_at=NULL,
                last_error=COALESCE(last_error,'Publisher lease expired after final attempt')
            WHERE published_at IS NULL AND dead_lettered_at IS NULL AND retry_count >= @MaxAttempts
                AND NOT (event_type = ANY(@excludedEventTypes))
                AND (lease_expires_at IS NULL OR lease_expires_at <= clock_timestamp());
            """, new { MaxAttempts, excludedEventTypes }, transaction, cancellationToken: cancellationToken));
        var token = Guid.NewGuid();
        var rows = await connection.QueryAsync<OutboxLease>(new CommandDefinition("""
            WITH candidates AS (
                SELECT m.id FROM outbox_messages m
                WHERE m.published_at IS NULL AND m.dead_lettered_at IS NULL
                    AND NOT (m.event_type = ANY(@excludedEventTypes))
                    AND m.retry_count < @MaxAttempts AND m.next_attempt_at <= clock_timestamp()
                    AND (m.lease_expires_at IS NULL OR m.lease_expires_at <= clock_timestamp())
                    AND NOT EXISTS (
                        SELECT 1 FROM outbox_messages earlier
                        WHERE earlier.partition_key=m.partition_key AND earlier.sequence<m.sequence
                            AND earlier.published_at IS NULL AND earlier.dead_lettered_at IS NULL
                            AND NOT (earlier.event_type = ANY(@excludedEventTypes))
                    )
                ORDER BY m.sequence FOR UPDATE OF m SKIP LOCKED LIMIT @batchSize
            )
            UPDATE outbox_messages m SET lease_token=@token, lease_expires_at=clock_timestamp()+@leaseDuration,
                retry_count=m.retry_count+1
            FROM candidates c WHERE m.id=c.id
            RETURNING m.id AS Id,m.event_type AS EventType,m.payload::text AS Payload,
                m.partition_key AS PartitionKey,m.retry_count AS RetryCount,m.lease_token AS LeaseToken;
            """, new { MaxAttempts, batchSize, token, leaseDuration, excludedEventTypes }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return rows.ToArray();
    }

    public async Task<bool> MarkPublishedAsync(Guid id, Guid leaseToken, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE outbox_messages SET published_at=clock_timestamp(), lease_token=NULL, lease_expires_at=NULL,last_error=NULL
            WHERE id=@id AND lease_token=@leaseToken AND lease_expires_at>clock_timestamp()
                AND published_at IS NULL AND dead_lettered_at IS NULL;
            """, new { id, leaseToken }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> MarkFailedAsync(Guid id, Guid leaseToken, string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE outbox_messages SET last_error=@error, lease_token=NULL, lease_expires_at=NULL,
                next_attempt_at=clock_timestamp()+make_interval(secs => LEAST(300,power(2,retry_count))::int),
                dead_lettered_at=CASE WHEN retry_count>=@MaxAttempts THEN clock_timestamp() ELSE NULL END
            WHERE id=@id AND lease_token=@leaseToken AND lease_expires_at>clock_timestamp()
                AND published_at IS NULL AND dead_lettered_at IS NULL;
            """, new { id, leaseToken, error = error.Length > 2000 ? error[..2000] : error, MaxAttempts },
            cancellationToken: cancellationToken)) == 1;
    }
}
