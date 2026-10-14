using Dapper;
using Npgsql;

namespace OrbitalSentinel.Infrastructure;

public sealed class JobScheduleRepository(NpgsqlDataSource dataSource)
{
    public async Task PreserveLegacyIngestionScheduleAsync(CancellationToken ct)
    {
        // When upgrading from the original three feeds, new feeds can start immediately
        // while those already downloaded retain their existing two-hour cooldown.
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO job_schedules(name,next_run_at)
            SELECT 'celestrak-group:' || g.name,s.next_run_at FROM job_schedules s
            CROSS JOIN unnest(ARRAY['stations','visual','iridium-33-debris']) g(name)
            WHERE s.name='celestrak-ingestion' ON CONFLICT DO NOTHING;
            """, cancellationToken: ct));
    }

    public async Task<bool> TryBeginAsync(string name, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || interval < TimeSpan.FromMinutes(1))
            throw new ArgumentException("A durable schedule requires a name and interval of at least one minute.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO job_schedules(name,next_run_at) VALUES (@name,clock_timestamp()+@interval)
            ON CONFLICT (name) DO UPDATE SET next_run_at=excluded.next_run_at
            WHERE job_schedules.next_run_at<=clock_timestamp();
            """, new { name, interval }, cancellationToken: cancellationToken)) == 1;
    }
}
