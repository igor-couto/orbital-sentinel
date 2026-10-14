using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;

namespace OrbitalSentinel.IntegrationTests;

public sealed class CelesTrakTests
{
    private const string ValidOmm = """
        {"OBJECT_NAME":"TEST SPACECRAFT","OBJECT_ID":"1998-067A","NORAD_CAT_ID":25544,
         "EPOCH":"2026-10-07T12:34:56.123456","MEAN_MOTION":15.5,"ECCENTRICITY":0.0001,
         "INCLINATION":51.6,"RA_OF_ASC_NODE":20,"ARG_OF_PERICENTER":30,"MEAN_ANOMALY":0,
         "BSTAR":0.00003,"MEAN_MOTION_DOT":0,"MEAN_MOTION_DDOT":0}
        """;

    [Theory]
    [InlineData(0, 1200)]
    [InlineData(500, 500)]
    [InlineData(750, 750)]
    public async Task Imports_all_distinct_objects_unless_an_explicit_limit_is_configured(int limit, int expected)
    {
        var objects = Enumerable.Range(1, 1200).Select(id =>
        {
            var row = JsonNode.Parse(ValidOmm)!.AsObject();
            row["NORAD_CAT_ID"] = id;
            return row.ToJsonString();
        });
        var handler = new FakeHandler((_, _) => Response("[" + string.Join(",", objects) + "]"));
        var rows = await Client(handler, max: limit).FetchAsync();
        Assert.Equal(expected, rows.Count);
        Assert.Equal(expected, rows.Select(x => x.NoradCatalogId).Distinct().Count());
    }

    [Fact]
    public void Public_group_discovery_accepts_only_GP_links_on_the_official_host()
    {
        const string page = """
            <a href="gp.php?GROUP=stations&amp;FORMAT=json">stations</a>
            <a href='gp.php?GROUP=active&FORMAT=json'>active</a>
            <a href='gp.php?GROUP=ACTIVE&FORMAT=csv'>duplicate</a>
            <a href='https://example.org/NORAD/elements/gp.php?GROUP=untrusted'>external</a>
            <a href='table.php?GROUP=not-a-feed'>table</a>
            <a href='gp.php?SPECIAL=GPZ'>not a group</a>
            """;
        Assert.Equal(new[] { "active", "stations" }, CelesTrakGroups.Parse(page));
        Assert.Throws<InvalidDataException>(() => CelesTrakGroups.Parse("<html>No feed links</html>"));
    }

    [Fact]
    public async Task Overlapping_groups_use_the_newest_elements_and_one_unavailable_group_does_not_block_the_rest()
    {
        var newer = JsonNode.Parse(ValidOmm)!.AsObject();
        newer["EPOCH"] = "2026-10-08T12:00:00Z";
        var handler = new FakeHandler((request, _) => request.RequestUri!.Query.Contains("missing", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Response($"[{(request.RequestUri.Query.Contains("newer", StringComparison.Ordinal) ? newer.ToJsonString() : ValidOmm)}]"));
        var result = await Client(handler).FetchAsync(["older", "missing", "newer"], true);
        Assert.Equal(DateTimeOffset.Parse("2026-10-08T12:00:00Z"), Assert.Single(result).Elements.EpochUtc);
        Assert.Equal(3, handler.Attempts);
    }

    [Fact]
    public async Task Partial_ingestion_stops_on_forbidden_responses_without_probing_more_groups()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).FetchAsync(["a", "b", "c"], true));
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Maps_OMM_numeric_strings_UTC_and_zero_values_with_ambiguous_type_left_unknown()
    {
        var source = JsonNode.Parse(ValidOmm)!.AsObject();
        source["MEAN_ANOMALY"] = "0";
        source["NORAD_CAT_ID"] = "100123";
        source["OBJECT_NAME"] = "A NAME WITH DEB AND R/B";
        var handler = new FakeHandler((_, _) => Response($"[{source.ToJsonString()}]"));
        var row = Assert.Single(await Client(handler).FetchAsync());
        Assert.Equal(100123, row.NoradCatalogId);
        Assert.Equal(TimeSpan.Zero, row.Elements.EpochUtc.Offset);
        Assert.Equal(0, row.Elements.MeanAnomalyDegrees);
        Assert.Equal("Unknown", row.ObjectType);
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData("IRIDIUM 33 DEB", "Debris")]
    [InlineData("COSMOS 2251 DEB", "Debris")]
    [InlineData("FREGAT DEB", "Debris")]
    [InlineData("BREEZE-M DEB [TANK]", "Debris")]
    [InlineData("IRIDIUM 33 DEB (ALTERNATE NAME)", "Debris")]
    [InlineData("BREEZE-M DEB [TANK] (ALTERNATE NAME)", "Debris")]
    [InlineData("  iridium 33 deb  ", "Debris")]
    [InlineData("CZ-4 R/B", "Rocket Body")]
    [InlineData("SL-12 R/B(1)", "Rocket Body")]
    [InlineData("SL-12 R/B(2)", "Rocket Body")]
    [InlineData("SL-12 R/B(3)", "Rocket Body")]
    [InlineData("SL-12 R/B(2) [STAGE]", "Rocket Body")]
    [InlineData("ISS (ZARYA)", "Unknown")]
    [InlineData("STARLINK-1234", "Unknown")]
    [InlineData("OBJECT PLAT", "Unknown")]
    [InlineData("DEBORAH", "Unknown")]
    [InlineData("OBJECT DEB-TEST", "Unknown")]
    [InlineData("OBJECT DEBRIS", "Unknown")]
    [InlineData("OBJECT DEB UNKNOWN", "Unknown")]
    [InlineData("OBJECT (IRIDIUM 33 DEB)", "Unknown")]
    [InlineData("OBJECT [DEB]", "Unknown")]
    [InlineData("OBJECT (ALIAS DEB", "Unknown")]
    [InlineData("OBJECT DEB [TANK", "Unknown")]
    [InlineData("OBJECT R/B(UNKNOWN)", "Unknown")]
    [InlineData("OBJECT R/B(0)", "Unknown")]
    [InlineData("OBJECT R/B DEB", "Unknown")]
    [InlineData("OBJECT PLAT DEB", "Unknown")]
    [InlineData("OBJECT & OTHER DEB", "Unknown")]
    public async Task Missing_type_uses_only_unambiguous_CelesTrak_name_markers(string name, string expected)
    {
        var source = JsonNode.Parse(ValidOmm)!.AsObject();
        source["OBJECT_NAME"] = name;
        var handler = new FakeHandler((_, _) => Response($"[{source.ToJsonString()}]"));
        var row = Assert.Single(await Client(handler).FetchAsync());
        Assert.Equal(expected, row.ObjectType);
    }

    [Theory]
    [InlineData(null, "Debris")]
    [InlineData("", "Debris")]
    [InlineData("  ", "Debris")]
    [InlineData("PAYLOAD", "PAYLOAD")]
    [InlineData("PAY", "PAY")]
    [InlineData("  ROCKET BODY  ", "ROCKET BODY")]
    [InlineData("DEB", "DEB")]
    [InlineData("R/B", "R/B")]
    [InlineData("Unknown", "Unknown")]
    [InlineData("UNK", "UNK")]
    [InlineData("OTHER", "OTHER")]
    public async Task Supplied_type_takes_precedence_over_name_fallback(string? type, string expected)
    {
        var source = JsonNode.Parse(ValidOmm)!.AsObject();
        source["OBJECT_NAME"] = "IRIDIUM 33 DEB";
        source["OBJECT_TYPE"] = type;
        var handler = new FakeHandler((_, _) => Response($"[{source.ToJsonString()}]"));
        Assert.Equal(expected, Assert.Single(await Client(handler).FetchAsync()).ObjectType);
    }

    [Theory]
    [InlineData("MEAN_ANOMALY")]
    [InlineData("BSTAR")]
    [InlineData("MEAN_MOTION_DOT")]
    [InlineData("MEAN_MOTION_DDOT")]
    [InlineData("NORAD_CAT_ID")]
    public async Task Missing_required_value_is_rejected_instead_of_defaulting_to_zero(string field)
    {
        var source = JsonNode.Parse(ValidOmm)!.AsObject();
        source.Remove(field);
        var handler = new FakeHandler((_, _) => Response($"[{source.ToJsonString()}]"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).FetchAsync());
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData("REF_FRAME", "ITRF")]
    [InlineData("CENTER_NAME", "MARS")]
    [InlineData("TIME_SYSTEM", "TAI")]
    [InlineData("MEAN_ELEMENT_THEORY", "OTHER")]
    public async Task Unsupported_OMM_conventions_are_rejected(string field, string value)
    {
        var source = JsonNode.Parse(ValidOmm)!.AsObject();
        source[field] = value;
        var handler = new FakeHandler((_, _) => Response($"[{source.ToJsonString()}]"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).FetchAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"message\":\"source unavailable\"}")]
    [InlineData("[]")]
    public async Task Malformed_empty_or_nonarray_payload_does_not_retry(string json)
    {
        var handler = new FakeHandler((_, _) => Response(json));
        await Assert.ThrowsAnyAsync<Exception>(() => Client(handler).FetchAsync());
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Invalid_entries_are_isolated_and_valid_source_type_is_preserved()
    {
        var source = JsonNode.Parse(ValidOmm)!.AsObject();
        source["OBJECT_TYPE"] = "ROCKET BODY";
        var handler = new FakeHandler((_, _) => Response($"[{{\"NORAD_CAT_ID\":5}},{source.ToJsonString()}]"));
        var accepted = Assert.Single(await Client(handler).FetchAsync());
        Assert.Equal("ROCKET BODY", accepted.ObjectType);
    }

    [Fact]
    public async Task Transient_failure_retries_but_attempts_are_finite()
    {
        var recovery = new FakeHandler((_, attempt) => attempt < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response($"[{ValidOmm}]"));
        Assert.Single(await Client(recovery).FetchAsync());
        Assert.Equal(3, recovery.Attempts);
        var outage = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(outage).FetchAsync());
        Assert.Equal(3, outage.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Permanent_HTTP_errors_stop_group_cycle_without_retry(HttpStatusCode status)
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(status));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler, ["stations", "visual"]).FetchAsync());
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Long_retry_after_aborts_instead_of_retrying_too_early()
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return response;
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).FetchAsync());
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData(4, new long[] { 1, 6, 2, 4 })]
    [InlineData(0, new long[] { 1, 6, 2, 4, 7, 3, 5, 8 })]
    public async Task Round_robin_selection_includes_groups_and_deduplicates_catalog_ids(int limit, long[] expected)
    {
        var handler = new FakeHandler((request, _) =>
        {
            Assert.Contains("FORMAT=JSON", request.RequestUri!.Query);
            var ids = request.RequestUri.Query.Contains("stations", StringComparison.Ordinal) ? new[] { 1, 2, 3 }
                : request.RequestUri.Query.Contains("visual", StringComparison.Ordinal) ? new[] { 1, 4, 5 } : new[] { 6, 7, 8 };
            var rows = ids.Select(id =>
            {
                var row = JsonNode.Parse(ValidOmm)!.AsObject();
                row["NORAD_CAT_ID"] = id;
                return row.ToJsonString();
            });
            return Response($"[{string.Join(',', rows)}]");
        });
        var client = Client(handler, ["stations", "visual", "iridium-33-debris"], limit);
        var result = await client.FetchAsync();
        Assert.Equal(expected.Select(x => (long?)x), result.Select(x => x.NoradCatalogId));
        Assert.Equal(3, handler.Attempts);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var handler = new FakeHandler((_, _) => Response($"[{ValidOmm}]"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).FetchAsync(cancelled.Token));
        Assert.InRange(handler.Attempts, 0, 1);
    }

    [Fact]
    public void Configuration_rejects_excessive_polling_and_negative_object_limits()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new CelesTrakClient(http,
            Options.Create(new CelesTrakOptions { RequestIntervalMinutes = 1 }), NullLogger<CelesTrakClient>.Instance));
        Assert.Throws<ArgumentException>(() => new CelesTrakClient(http,
            Options.Create(new CelesTrakOptions { MaxObjects = -1 }), NullLogger<CelesTrakClient>.Instance));
    }

    private static CelesTrakClient Client(FakeHandler handler, string[]? groups = null, int max = 0) =>
        new(new HttpClient(handler), Options.Create(new CelesTrakOptions
        {
            Groups = groups ?? ["stations"], MaxObjects = max
        }), NullLogger<CelesTrakClient>.Instance);

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request, ++Attempts));
        }
    }
}
