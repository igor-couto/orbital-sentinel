using System.Net;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;

namespace OrbitalSentinel.IntegrationTests;

public sealed class SatelliteCatalogTests
{
    [Fact]
    public async Task Reads_six_digit_ids_and_optional_catalog_fields_without_inventing_metadata()
    {
        var handler = new Handler("""
            [{"NORAD_CAT_ID":"100123","OBJECT_TYPE":"PAY","OWNER":"US","LAUNCH_DATE":"2026-10-13",
              "LAUNCH_SITE":"AFETR","OPS_STATUS_CODE":"+"},{"NORAD_CAT_ID":25544}]
            """);
        var client = new SatelliteCatalogClient(new HttpClient(handler), Options.Create(new SatelliteCatalogOptions()));
        var records = await client.FetchGroupAsync("stations");
        Assert.Equal(100123, records[0].NoradCatalogId);
        Assert.Equal("US", records[0].OwnerCode);
        Assert.Equal("+", records[0].OperationalStatusCode);
        Assert.Null(records[1].OwnerCode);
        Assert.Contains("GROUP=stations&FORMAT=JSON", handler.Url);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"NORAD_CAT_ID\":0}]")]
    [InlineData("[{\"NORAD_CAT_ID\":25544,\"LAUNCH_DATE\":\"yesterday\"}]")]
    public async Task Rejects_bad_or_empty_downloads_so_the_existing_cache_is_not_replaced(string body)
    {
        var client = new SatelliteCatalogClient(new HttpClient(new Handler(body)), Options.Create(new SatelliteCatalogOptions()));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchGroupAsync("stations"));
    }

    [Fact]
    public async Task Does_not_retry_source_errors()
    {
        var handler = new Handler("unavailable", HttpStatusCode.ServiceUnavailable);
        var client = new SatelliteCatalogClient(new HttpClient(handler), Options.Create(new SatelliteCatalogOptions()));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchGroupAsync("stations"));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string Url { get; private set; } = "";
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}

[Collection("PostgreSQL")]
public sealed class CatalogMetadataPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Caches_group_membership_and_latest_metadata_without_creating_orbital_objects()
    {
        var repository = new CatalogMetadataRepository(fixture.DataSource);
        var epoch = DateTimeOffset.UtcNow;
        var first = new SatelliteCatalogRecord(25544, "PAY", "ISS", "1998-11-20", "TYMSC", "+");
        await repository.SaveGroupAsync("stations", [first], epoch);
        await repository.SaveGroupAsync("visual", [first with { OperationalStatusCode = "P" }], epoch.AddMinutes(1));
        var record = await repository.GetAsync(25544);
        Assert.Equal(new[] { "stations", "visual" }, record!.Groups);
        Assert.Equal("P", record.OperationalStatusCode);
        Assert.Equal("ISS", record.OwnerCode);
        Assert.Equal(0, (await new CatalogRepository(fixture.DataSource).ListAsync(1, 10, null)).TotalCount);
        var catalog = new CatalogRepository(fixture.DataSource);
        var elements = new OrbitalSentinel.Core.OrbitalElements(epoch, 15.5, 0.001, 50, 10, 20, 30, 0, 0, 0);
        await catalog.UpsertAsync(new CatalogImport(null, 25544, "Test", "1998-067A", "Unknown", elements));
        var snapshot = Assert.Single((await catalog.ListAsync(1, 10, null)).Items);
        Assert.Equal("PAY", snapshot.Object.ObjectType);
        Assert.Equal("PAY", (await catalog.GetAsync(snapshot.Object.Id))!.Object.ObjectType);
        // Existing definite classifications remain authoritative; enrichment fills only unknowns.
        await catalog.UpsertAsync(new CatalogImport(null, 25544, "Test", "1998-067A", "Rocket Body", elements));
        Assert.Equal("Rocket Body", (await catalog.GetAsync(snapshot.Object.Id))!.Object.ObjectType);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveGroupAsync("visual", [], epoch));
        Assert.Equal("P", (await repository.GetAsync(25544))!.OperationalStatusCode);
        await repository.SaveGroupAsync("visual", [first with { NoradCatalogId = 100123 }], epoch.AddMinutes(2));
        Assert.Equal(new[] { "stations" }, (await repository.GetAsync(25544))!.Groups);
        Assert.Null(await repository.GetAsync(999999));
    }
}
