using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OrbitalSentinel.Infrastructure;

public sealed class SatelliteCatalogOptions
{
    public string BaseUrl { get; set; } = "https://celestrak.org/satcat/records.php";
    public int RequestIntervalMinutes { get; set; } = 1440;
}

/// <summary>Optional enrichment, fetched in groups, independently of orbital ingestion. No immediate retries.</summary>
public sealed class SatelliteCatalogClient(HttpClient httpClient, IOptions<SatelliteCatalogOptions> options)
{
    public async Task<IReadOnlyList<SatelliteCatalogRecord>> FetchGroupAsync(string group,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await httpClient.GetAsync(
            $"{options.Value.BaseUrl}?GROUP={Uri.EscapeDataString(group)}&FORMAT=JSON",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        const int maximumBytes = 64 * 1024 * 1024;
        await response.Content.LoadIntoBufferAsync(maximumBytes, timeout.Token);
        var records = JsonSerializer.Deserialize<SatelliteCatalogRecord[]>(
            await response.Content.ReadAsStringAsync(timeout.Token),
            new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString });
        records = records?.Select(r => r is null ? r! : r with
        {
            ObjectType = Clean(r.ObjectType), OwnerCode = Clean(r.OwnerCode), LaunchDate = Clean(r.LaunchDate),
            LaunchSiteCode = Clean(r.LaunchSiteCode), OperationalStatusCode = Clean(r.OperationalStatusCode)
        }).ToArray();
        if (records is not { Length: > 0 } || records.Any(r => r is null || r.NoradCatalogId <= 0
                || !ValidText(r.OwnerCode, 20) || !ValidText(r.LaunchSiteCode, 20)
                || !ValidText(r.ObjectType, 100) || !ValidText(r.OperationalStatusCode, 10)
                || (r.LaunchDate is { Length: > 0 } date && !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out _))))
            throw new InvalidDataException("The SATCAT response did not contain valid catalog records; cached metadata is retained.");
        return records.DistinctBy(r => r.NoradCatalogId).ToArray();
    }

    private static bool ValidText(string? text, int maximum) => text is null || text.Length <= maximum;
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
