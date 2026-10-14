using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrbitalSentinel.Infrastructure;

/// <summary>Discovers only GP group links published by CelesTrak; never follows arbitrary page links.</summary>
public sealed partial class CelesTrakGroups(HttpClient http, IOptions<CelesTrakOptions> options,
    ILogger<CelesTrakGroups> logger)
{
    // Public GP groups verified at https://celestrak.org/NORAD/elements/ on 2026-10-13.
    // Used on a cold start if the index is temporarily unavailable, not as a catalog-size cap.
    public static string[] Published => ["active", "amateur", "analyst", "argos", "beidou",
        "cosmos-2251-debris", "cubesat", "dmc", "education", "engineering", "eutelsat",
        "fengyun-1c-debris", "galileo", "geo", "geodetic", "glo-ops", "globalstar", "gnss",
        "gps-ops", "hulianwang", "intelsat", "iridium-33-debris", "iridium-NEXT", "kuiper",
        "last-30-days", "military", "oneweb", "orbcomm", "other-comm", "planet", "qianfan",
        "radar", "resource", "sar", "sarsat", "satnogs", "sbas", "science", "ses", "spire",
        "starlink", "stations", "tdrss", "telesat", "visual", "weather", "x-comm"];

    private readonly SemaphoreSlim gate = new(1);
    private string[]? cached;
    private DateTimeOffset refreshAfter;
    private static readonly Uri IndexUri = new("https://celestrak.org/NORAD/elements/");

    public async Task<string[]> GetAsync(CancellationToken ct = default)
    {
        if (!options.Value.DiscoverPublicGroups) return options.Value.Groups;
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null && refreshAfter > DateTimeOffset.UtcNow) return cached;
            // Also back off after a failed discovery, while preserving the last known group list.
            refreshAfter = DateTimeOffset.UtcNow.AddHours(24);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await http.GetAsync(IndexUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
                cached = Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                logger.LogInformation("Discovered {GroupCount} public CelesTrak GP groups", cached.Length);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or InvalidDataException)
            {
                logger.LogWarning(ex, "CelesTrak group discovery unavailable; using the last known public group list");
                cached ??= Published;
            }
            return cached;
        }
        finally { gate.Release(); }
    }

    public static string[] Parse(string html)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HrefPattern().Matches(html))
        {
            if (!Uri.TryCreate(IndexUri, WebUtility.HtmlDecode(match.Groups[1].Value), out var link)
                || link.Scheme != IndexUri.Scheme || link.Host != IndexUri.Host
                || link.AbsolutePath != "/NORAD/elements/gp.php") continue;
            foreach (var field in link.Query.TrimStart('?').Split('&'))
            {
                var pair = field.Split('=', 2);
                if (pair.Length == 2 && pair[0].Equals("GROUP", StringComparison.OrdinalIgnoreCase)
                    && GroupPattern().IsMatch(pair[1])) groups.Add(pair[1]);
            }
        }
        if (groups.Count == 0) throw new InvalidDataException("No public GP groups found on the CelesTrak index.");
        return groups.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [GeneratedRegex("href\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HrefPattern();
    [GeneratedRegex("^[a-zA-Z0-9-]{1,80}$", RegexOptions.None, 1000)]
    private static partial Regex GroupPattern();
}
