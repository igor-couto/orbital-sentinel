using Dapper;
using Npgsql;
using OrbitalSentinel.Infrastructure;
using Testcontainers.PostgreSql;

namespace OrbitalSentinel.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.6-alpine").Build();
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        DataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await new DatabaseMigrator(DataSource).MigrateAsync();
    }

    public async Task ResetAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            TRUNCATE outbox_messages, processed_messages, trajectory_predictions, orbital_element_sets,
                orbital_objects, consumer_failures, job_schedules RESTART IDENTITY CASCADE;
            """);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await container.DisposeAsync();
    }
}

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
