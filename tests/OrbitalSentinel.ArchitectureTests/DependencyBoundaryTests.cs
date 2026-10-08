using System.Reflection;
using OrbitalSentinel.Api;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Propagation;
using OrbitalSentinel.Runtime;
using OrbitalSentinel.Workers;
using Xunit;

namespace OrbitalSentinel.ArchitectureTests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void DomainAndCalculationModulesDoNotDependOnHostingOrPersistence()
    {
        AssertInternalReferences(typeof(OrbitalElements).Assembly, []);
        AssertInternalReferences(typeof(Sgp4OrbitPropagator).Assembly, ["OrbitalSentinel.Core"]);
        AssertInternalReferences(typeof(CatalogRepository).Assembly, ["OrbitalSentinel.Core"]);
    }

    [Fact]
    public void ApiAndWorkerHostsRemainIndependentCompositionRoots()
    {
        AssertInternalReferences(typeof(ApiAssemblyMarker).Assembly,
            ["OrbitalSentinel.Core", "OrbitalSentinel.Infrastructure", "OrbitalSentinel.Runtime"]);
        AssertInternalReferences(typeof(PropagationWorker).Assembly,
            ["OrbitalSentinel.Core", "OrbitalSentinel.Infrastructure", "OrbitalSentinel.Runtime"]);
        AssertInternalReferences(typeof(RuntimeServices).Assembly,
            ["OrbitalSentinel.Core", "OrbitalSentinel.Infrastructure", "OrbitalSentinel.Propagation"]);
    }

    [Fact]
    public void OnlyThePropagationAdapterDependsOnTheSgp4Library()
    {
        var library = typeof(SGPdotNET.Observation.Satellite).Assembly.GetName().Name;
        Assert.Contains(library, ReferenceNames(typeof(Sgp4OrbitPropagator).Assembly));
        Assembly[] isolatedAssemblies =
        [
            typeof(OrbitalElements).Assembly,
            typeof(CatalogRepository).Assembly,
            typeof(RuntimeServices).Assembly,
            typeof(ApiAssemblyMarker).Assembly,
            typeof(PropagationWorker).Assembly
        ];
        Assert.All(isolatedAssemblies, assembly => Assert.DoesNotContain(library, ReferenceNames(assembly)));
    }

    private static void AssertInternalReferences(Assembly assembly, string[] allowed)
    {
        var internalReferences = ReferenceNames(assembly).Where(name =>
            name.StartsWith("OrbitalSentinel.", StringComparison.Ordinal));
        Assert.All(internalReferences, name => Assert.Contains(name, allowed));
    }

    private static IEnumerable<string> ReferenceNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(reference => reference.Name!);
}
