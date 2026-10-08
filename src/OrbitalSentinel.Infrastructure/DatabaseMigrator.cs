using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace OrbitalSentinel.Infrastructure;

public sealed class DatabaseMigrator(NpgsqlDataSource dataSource)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Transaction-scoped lock serializes concurrent application starts, including first boot.
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(674681997823901);",
            transaction: transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                name text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now()
            );
            """, transaction: transaction, cancellationToken: cancellationToken));
        var applied = (await connection.QueryAsync<MigrationRow>(new CommandDefinition(
            "SELECT name AS Name, checksum AS Checksum FROM schema_migrations;", transaction: transaction,
            cancellationToken: cancellationToken))).ToDictionary(x => x.Name, x => x.Checksum);
        var assembly = typeof(DatabaseMigrator).Assembly;
        var names = assembly.GetManifestResourceNames().Where(x => x.Contains(".Migrations.", StringComparison.Ordinal)
            && x.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (applied.Keys.Except(names).Any())
            throw new InvalidOperationException("The database contains migrations absent from this application build.");
        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing migration resource {name}.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = await reader.ReadToEndAsync(cancellationToken);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            if (applied.TryGetValue(name, out var previousChecksum))
            {
                if (previousChecksum != checksum)
                    throw new InvalidOperationException($"Migration {name} was modified after it was applied.");
                continue;
            }
            await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO schema_migrations(name, checksum) VALUES (@name, @checksum);",
                new { name, checksum }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record MigrationRow(string Name, string Checksum);
}
