using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Propagation;

namespace OrbitalSentinel.Runtime;

public static class RuntimeServices
{
    public static IServiceCollection AddOrbitalRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres must be configured.");
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<CatalogRepository>();
        services.AddSingleton<TrajectoryRepository>();
        services.AddSingleton<OutboxRepository>();
        services.AddSingleton<ConsumerFailureRepository>();
        services.AddSingleton<JobScheduleRepository>();
        services.AddSingleton<ConjunctionRepository>();
        services.AddSingleton<IConjunctionScanner, ConjunctionScanner>();
        services.AddSingleton<ConjunctionApplication>();
        services.AddSingleton<IOrbitPropagator, Sgp4OrbitPropagator>();
        services.AddOptions<KafkaOptions>().Bind(configuration.GetSection("Kafka"))
            .ValidateDataAnnotations().Validate(x => new[] { x.OrbitTopic, x.TrajectoryTopic, x.DeadLetterTopic, x.PredictionRunTopic, x.ConjunctionTopic }.Distinct().Count() == 5,
                "Kafka topics must be distinct.").ValidateOnStart();
        services.AddOptions<PredictionOptions>().Bind(configuration.GetSection("Prediction"))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ConjunctionOptions>().Bind(configuration.GetSection("ConjunctionScreening"))
            .ValidateDataAnnotations().Validate(x => x.ReportingDistanceKm <= x.CandidateDistanceKm,
                "Reporting distance must not exceed the candidate screening distance.").ValidateOnStart();
        services.AddOptions<CelesTrakOptions>()
            // The configuration binder appends to initialized arrays. Clear defaults before binding.
            .Configure(x => x.Groups = [])
            .Bind(configuration.GetSection("CelesTrak"))
            .PostConfigure(x => { if (x.Groups.Length == 0) x.Groups = new CelesTrakOptions().Groups; })
            .Validate(x => x.RequestIntervalMinutes >= 120, "CelesTrak polling must be at least 120 minutes apart.")
            .Validate(x => x.MaxObjects is >= 1 and <= 200, "Track 1 to 200 objects.")
            .Validate(x => x.Groups.Length is > 0 and <= 5 && x.Groups.All(g => !string.IsNullOrWhiteSpace(g)), "Configure 1 to 5 groups.")
            .Validate(x => Uri.TryCreate(x.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.Query), "Use an HTTPS CelesTrak URL without a query string.")
            .Validate(x => x.TimeoutSeconds is >= 1 and <= 60 && x.MaxRetries is >= 0 and <= 3, "HTTP retries and timeouts must be bounded.")
            .ValidateOnStart();
        services.AddHttpClient<CelesTrakClient>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan; // Per-attempt cancellation is owned by the source adapter.
            client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OrbitalSentinel/0.1 (educational-orbit-viewer)");
        });
        services.AddSingleton<IProducer<string, string>>(sp => new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = sp.GetRequiredService<IOptions<KafkaOptions>>().Value.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 10000,
            SocketTimeoutMs = 10000
        }).Build());
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres").AddCheck<KafkaHealthCheck>("kafka");
        return services;
    }
}

internal sealed class PostgresHealthCheck(NpgsqlDataSource database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unavailable.");
        }
    }
}

internal sealed class KafkaHealthCheck(IOptions<KafkaOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers }).Build();
            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(3));
            return Task.FromResult(metadata.Brokers.Count > 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("No Kafka brokers available."));
        }
        catch (KafkaException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka unavailable."));
        }
    }
}
