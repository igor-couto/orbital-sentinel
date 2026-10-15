using System.Text.Json;
using Dapper;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;

namespace OrbitalSentinel.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class CatalogBootstrapTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Returns_the_entire_catalog_with_current_revisions_without_loading_element_payloads()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO orbital_objects(id,norad_catalog_id,name,international_designator,object_type,current_version,updated_at)
            SELECT gen_random_uuid(),100000+i,'Object '||lpad(i::text,6,'0'),NULL,'Unknown',2,now()
            FROM generate_series(1,20225) i;
            INSERT INTO orbital_element_sets(orbital_object_id,version,epoch_utc,elements,fingerprint,ingested_at)
            SELECT id,version,now(),'"not an orbital element set"'::jsonb,'unused',now()
            FROM orbital_objects CROSS JOIN generate_series(1,2) version;
            """);

        var snapshot = await new CatalogRepository(fixture.DataSource).GetBootstrapAsync();

        Assert.Equal(20225, snapshot.TotalCount);
        Assert.Equal(20225, snapshot.Items.Count);
        Assert.Equal(20225, snapshot.Items.Select(x => x.Object.Id).Distinct().Count());
        Assert.Equal("Object 000001", snapshot.Items[0].Object.Name);
        Assert.Equal("Object 020225", snapshot.Items[^1].Object.Name);
        Assert.All(snapshot.Items, x => Assert.Equal(2, x.Version));
        var json = JsonSerializer.SerializeToElement(snapshot, Serialization.Options);
        Assert.Equal(new[] { "object", "version" }, json.GetProperty("items")[0].EnumerateObject().Select(x => x.Name));
        Assert.Equal(new[] { "id", "noradCatalogId", "name", "internationalDesignator", "objectType" },
            json.GetProperty("items")[0].GetProperty("object").EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public async Task Preserves_latest_SATCAT_type_fallback_and_explicit_classifications()
    {
        var catalog = new CatalogRepository(fixture.DataSource);
        var metadata = new CatalogMetadataRepository(fixture.DataSource);
        var now = DateTimeOffset.UtcNow;
        var elements = new OrbitalElements(now, 15.5, 0.001, 50, 10, 20, 30, 0, 0, 0);
        foreach (var (norad, type) in new[] { (1, "Unknown"), (2, "UNK"), (3, "Debris"), (4, "Unknown") })
            await catalog.UpsertAsync(new CatalogImport(null, norad, $"Object {norad}", null, type, elements));
        var internalId = Guid.NewGuid();
        await catalog.UpsertAsync(new CatalogImport(internalId, null, "Internal object", null, "Unknown", elements));
        await metadata.SaveGroupAsync("old", [new SatelliteCatalogRecord(1, "DEB", null, null, null, null)], now);
        await metadata.SaveGroupAsync("new", [
            new SatelliteCatalogRecord(1, "PAY", null, null, null, null),
            new SatelliteCatalogRecord(2, "R/B", null, null, null, null),
            new SatelliteCatalogRecord(3, "PAY", null, null, null, null),
            new SatelliteCatalogRecord(4, "UNK", null, null, null, null)
        ], now.AddMinutes(1));

        var snapshot = await catalog.GetBootstrapAsync();

        Assert.Equal(5, snapshot.TotalCount);
        Assert.Equal("PAY", snapshot.Items.Single(x => x.Object.NoradCatalogId == 1).Object.ObjectType);
        Assert.Equal("R/B", snapshot.Items.Single(x => x.Object.NoradCatalogId == 2).Object.ObjectType);
        Assert.Equal("Debris", snapshot.Items.Single(x => x.Object.NoradCatalogId == 3).Object.ObjectType);
        Assert.Equal("Unknown", snapshot.Items.Single(x => x.Object.NoradCatalogId == 4).Object.ObjectType);
        Assert.Null(snapshot.Items.Single(x => x.Object.Id == internalId).Object.NoradCatalogId);
    }

    [Fact]
    public async Task Empty_catalog_has_a_consistent_zero_count()
    {
        var snapshot = await new CatalogRepository(fixture.DataSource).GetBootstrapAsync();
        Assert.Empty(snapshot.Items);
        Assert.Equal(0, snapshot.TotalCount);
    }
}
