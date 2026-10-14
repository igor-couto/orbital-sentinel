using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.Infrastructure;

public sealed class CelesTrakOptions
{
    public string BaseUrl { get; set; } = "https://celestrak.org/NORAD/elements/gp.php";
    public bool DiscoverPublicGroups { get; set; } = true;
    public string[] Groups { get; set; } = CelesTrakGroups.Published;
    // Zero imports every valid, distinct object from the configured groups.
    public int MaxObjects { get; set; } = 0;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public int RequestIntervalMinutes { get; set; } = 120;
}

public sealed class CelesTrakClient
{
    private const int MaxResponseBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions SourceJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly HttpClient httpClient;
    private readonly CelesTrakOptions options;
    private readonly ILogger<CelesTrakClient> logger;

    public CelesTrakClient(HttpClient httpClient, IOptions<CelesTrakOptions> options, ILogger<CelesTrakClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
        if (!Uri.TryCreate(this.options.BaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.Query)
            || this.options.MaxObjects < 0 || this.options.TimeoutSeconds is < 1 or > 120
            || this.options.MaxRetries is < 0 or > 3 || this.options.RequestIntervalMinutes < 120
            || this.options.Groups.Length < 1
            || this.options.Groups.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid CelesTrak configuration: use a nonnegative object limit (0 means unlimited), nonempty groups, finite retries and a >=120 minute poll interval.", nameof(options));
    }

    public async Task<IReadOnlyList<CatalogImport>> FetchAsync(CancellationToken cancellationToken = default)
        => await FetchAsync(options.Groups, false, cancellationToken);

    public async Task<IReadOnlyList<CatalogImport>> FetchAsync(string[] groupNames, bool allowPartial,
        CancellationToken cancellationToken = default)
    {
        var groups = new List<IReadOnlyList<CatalogImport>>();
        foreach (var group in groupNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { groups.Add(await FetchGroupAsync(group, cancellationToken)); }
            catch (Exception ex) when (allowPartial && !cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException)
            {
                logger.LogWarning(ex, "CelesTrak group {Group} unavailable; its stored objects are retained", group);
                // A block or rate limit can apply to the whole IP. Do not keep probing other groups.
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }) break;
            }
            if (allowPartial) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        var latest = groups.SelectMany(g => g).GroupBy(x => x.NoradCatalogId)
            .ToDictionary(g => g.Key!.Value, g => g.MaxBy(x => x.Elements.EpochUtc)!);
        var selected = new List<CatalogImport>();
        var seen = new HashSet<long>();
        var limit = options.MaxObjects == 0 ? int.MaxValue : options.MaxObjects;
        for (var index = 0; selected.Count < limit && groups.Any(x => x.Count > index); index++)
        {
            foreach (var group in groups)
            {
                if (index >= group.Count || selected.Count >= limit) continue;
                var candidate = group[index];
                if (candidate.NoradCatalogId is { } id && seen.Add(id)) selected.Add(latest[id]);
            }
        }
        if (selected.Count == 0)
            throw new InvalidDataException("CelesTrak returned no valid orbital objects; the existing catalog is retained.");
        return selected;
    }

    private async Task<IReadOnlyList<CatalogImport>> FetchGroupAsync(string group, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{options.BaseUrl}?GROUP={Uri.EscapeDataString(group)}&FORMAT=JSON");
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryDelay = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("OrbitalSentinel/1.0");
                request.Headers.Accept.ParseAdd("application/json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (IsTransient(response.StatusCode) && attempt < options.MaxRetries)
                {
                    var requestedDelay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                    // Do not shorten a long Retry-After and accidentally hammer the source.
                    // Abort this cycle instead; the durable schedule controls the next poll.
                    if (requestedDelay > TimeSpan.FromSeconds(30))
                        throw new HttpRequestException("CelesTrak requested a retry delay beyond this ingestion cycle's budget.",
                            null, response.StatusCode);
                    retryDelay = requestedDelay is { } delay && delay > TimeSpan.Zero ? delay : Backoff(attempt);
                    logger.LogWarning("CelesTrak group {Group} returned {StatusCode}; retry {Attempt} of {MaxRetries}",
                        group, (int)response.StatusCode, attempt + 1, options.MaxRetries);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxResponseBytes)
                        throw new InvalidDataException("CelesTrak response exceeds the 64 MiB safety limit.");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[16384];
                    while (true)
                    {
                        var count = await stream.ReadAsync(chunk, timeout.Token);
                        if (count == 0) break;
                        if (buffer.Length + count > MaxResponseBytes)
                            throw new InvalidDataException("CelesTrak response exceeds the 64 MiB safety limit.");
                        await buffer.WriteAsync(chunk.AsMemory(0, count), timeout.Token);
                    }
                    buffer.Position = 0;
                    using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("CelesTrak OMM JSON must be an array.");
                    var result = new List<CatalogImport>();
                    var rejected = 0;
                    foreach (var entry in document.RootElement.EnumerateArray())
                    {
                        try
                        {
                            var source = entry.Deserialize<CelesTrakOmm>(SourceJson)
                                ?? throw new InvalidDataException("Null OMM entry.");
                            result.Add(Map(source));
                        }
                        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
                        {
                            rejected++;
                            logger.LogWarning("Rejected invalid CelesTrak OMM entry in group {Group}: {Reason}", group, exception.Message);
                        }
                    }
                    logger.LogInformation("CelesTrak group {Group}: accepted {Accepted}, rejected {Rejected}", group, result.Count, rejected);
                    if (result.Count == 0)
                        throw new InvalidDataException($"CelesTrak group {group} contained no valid orbital objects.");
                    return result;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < options.MaxRetries)
            {
                retryDelay = Backoff(attempt);
                logger.LogWarning("CelesTrak group {Group} timed out; retry {Attempt}", group, attempt + 1);
            }
            catch (HttpRequestException exception) when (exception.StatusCode is null && attempt < options.MaxRetries)
            {
                retryDelay = Backoff(attempt);
                logger.LogWarning("CelesTrak group {Group} transport failure; retry {Attempt}: {Reason}", group, attempt + 1, exception.Message);
            }
            await Task.Delay(retryDelay ?? throw new InvalidOperationException("Missing retry delay."), cancellationToken);
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt) + Random.Shared.Next(0, 100));
    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static CatalogImport Map(CelesTrakOmm source)
    {
        EnsureOptional(source.CenterName, "EARTH", "CENTER_NAME");
        EnsureOptional(source.ReferenceFrame, "TEME", "REF_FRAME");
        EnsureOptional(source.TimeSystem, "UTC", "TIME_SYSTEM");
        EnsureOptional(source.MeanElementTheory, "SGP4", "MEAN_ELEMENT_THEORY");
        if (source.NoradCatalogId is null or <= 0)
            throw new InvalidDataException("NORAD_CAT_ID must be supplied by CelesTrak.");
        if (!DateTimeOffset.TryParse(source.Epoch, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var epoch))
            throw new InvalidDataException("A valid UTC EPOCH is required.");
        var elements = new OrbitalElements(epoch, Required(source.MeanMotion, "MEAN_MOTION"),
            Required(source.Eccentricity, "ECCENTRICITY"), Required(source.Inclination, "INCLINATION"),
            Required(source.RightAscension, "RA_OF_ASC_NODE"), Required(source.ArgumentOfPericenter, "ARG_OF_PERICENTER"),
            Required(source.MeanAnomaly, "MEAN_ANOMALY"), Required(source.BStar, "BSTAR"),
            Required(source.MeanMotionDot, "MEAN_MOTION_DOT"), Required(source.MeanMotionDdot, "MEAN_MOTION_DDOT"));
        elements.Validate();
        var name = string.IsNullOrWhiteSpace(source.ObjectName) ? $"NORAD {source.NoradCatalogId}" : source.ObjectName.Trim();
        if (name.Length > 200) throw new InvalidDataException("OBJECT_NAME exceeds 200 characters.");
        var objectType = CelesTrakObjectClassification.Resolve(name, source.ObjectType);
        if (objectType.Length > 100) throw new InvalidDataException("OBJECT_TYPE exceeds 100 characters.");
        return new CatalogImport(null, source.NoradCatalogId, name, source.ObjectId, objectType, elements);
    }

    private static double Required(double? value, string field) => value is { } number && double.IsFinite(number)
        ? number : throw new InvalidDataException($"{field} is required and must be finite.");

    private static void EnsureOptional(string? value, string expected, string field)
    {
        if (value is not null && !string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported {field}; expected {expected}.");
    }
}

// Source-only DTO: nullable numeric fields distinguish legitimate zero values from missing OMM data.
internal sealed class CelesTrakOmm
{
    [JsonPropertyName("OBJECT_NAME")] public string? ObjectName { get; init; }
    [JsonPropertyName("OBJECT_ID")] public string? ObjectId { get; init; }
    [JsonPropertyName("OBJECT_TYPE")] public string? ObjectType { get; init; }
    [JsonPropertyName("NORAD_CAT_ID")] public long? NoradCatalogId { get; init; }
    [JsonPropertyName("EPOCH")] public string? Epoch { get; init; }
    [JsonPropertyName("MEAN_MOTION")] public double? MeanMotion { get; init; }
    [JsonPropertyName("ECCENTRICITY")] public double? Eccentricity { get; init; }
    [JsonPropertyName("INCLINATION")] public double? Inclination { get; init; }
    [JsonPropertyName("RA_OF_ASC_NODE")] public double? RightAscension { get; init; }
    [JsonPropertyName("ARG_OF_PERICENTER")] public double? ArgumentOfPericenter { get; init; }
    [JsonPropertyName("MEAN_ANOMALY")] public double? MeanAnomaly { get; init; }
    [JsonPropertyName("BSTAR")] public double? BStar { get; init; }
    [JsonPropertyName("MEAN_MOTION_DOT")] public double? MeanMotionDot { get; init; }
    [JsonPropertyName("MEAN_MOTION_DDOT")] public double? MeanMotionDdot { get; init; }
    [JsonPropertyName("CENTER_NAME")] public string? CenterName { get; init; }
    [JsonPropertyName("REF_FRAME")] public string? ReferenceFrame { get; init; }
    [JsonPropertyName("TIME_SYSTEM")] public string? TimeSystem { get; init; }
    [JsonPropertyName("MEAN_ELEMENT_THEORY")] public string? MeanElementTheory { get; init; }
}
