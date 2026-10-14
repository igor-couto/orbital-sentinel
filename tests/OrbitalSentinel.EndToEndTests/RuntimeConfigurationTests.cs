using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;
using Xunit;

namespace OrbitalSentinel.EndToEndTests;

public sealed class RuntimeConfigurationTests
{
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
