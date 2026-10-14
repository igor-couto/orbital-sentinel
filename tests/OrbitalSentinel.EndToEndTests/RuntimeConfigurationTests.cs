using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;
using OrbitalSentinel.Workers;
using Xunit;

namespace OrbitalSentinel.EndToEndTests;

public sealed class RuntimeConfigurationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Trajectory_worker_is_opt_in_without_disabling_ingestion_or_screening(string? setting, bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Prediction:PrecomputeTrajectories"] = setting
        }).Build();
        var services = new ServiceCollection().AddOrbitalWorkers(configuration);
        var workers = services.Where(x => x.ServiceType == typeof(IHostedService)).Select(x => x.ImplementationType).ToArray();
        Assert.Equal(enabled, workers.Contains(typeof(PropagationWorker)));
        Assert.Contains(typeof(IngestionWorker), workers);
        Assert.Contains(typeof(CatalogMetadataWorker), workers);
        Assert.Contains(typeof(OutboxPublisher), workers);
        Assert.Contains(typeof(PredictionRunCoordinator), workers);
        Assert.Contains(typeof(PredictionRunConsumer), workers);
        Assert.Contains(typeof(ConjunctionWorker), workers);
    }

    [Fact]
    public void ExplicitGroupsReplaceDefaultsInsteadOfAppendingToThem()
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=postgres;Password=test",
            ["CelesTrak:DiscoverPublicGroups"] = "false",
            ["CelesTrak:Groups:0"] = "stations",
            ["CelesTrak:Groups:1"] = "visual",
            ["CelesTrak:Groups:2"] = "iridium-33-debris"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var services = new ServiceCollection().AddOrbitalRuntime(configuration).BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<CelesTrakOptions>>().Value;

        Assert.Equal(new[] { "stations", "visual", "iridium-33-debris" }, options.Groups);
    }

    [Fact]
    public void DefaultsDiscoverAllPublicGroupsWithAnUnlimitedCatalog()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=postgres;Password=test"
        }).Build();
        using var services = new ServiceCollection().AddOrbitalRuntime(configuration).BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<CelesTrakOptions>>().Value;

        Assert.True(options.DiscoverPublicGroups);
        Assert.Equal(CelesTrakGroups.Published, options.Groups);
        Assert.Equal(0, options.MaxObjects);
        Assert.False(services.GetRequiredService<IOptions<PredictionOptions>>().Value.PrecomputeTrajectories);
    }
}
