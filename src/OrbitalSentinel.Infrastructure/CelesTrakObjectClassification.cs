using System.Text.RegularExpressions;

namespace OrbitalSentinel.Infrastructure;

internal static partial class CelesTrakObjectClassification
{
    public static string Resolve(string name, string? sourceType)
    {
        // Even an explicit unknown/unrecognized source type takes precedence.
        if (!string.IsNullOrWhiteSpace(sourceType)) return sourceType.Trim();

        // An ampersand denotes attached objects, which may have different types.
        if (name.Contains('&')) return "Unknown";

        // Parenthesized aliases and bracketed descriptions are not category evidence.
        // Preserve adjacent stage numbers in markers such as R/B(2).
        var primaryName = TrailingAnnotations().Replace(name.Trim(), "");
        var markers = TypeMarkers().Matches(primaryName);
        if (markers.Count != 1) return "Unknown";
        var marker = markers[0];
        if (marker.Index + marker.Length != primaryName.Length
            || primaryName.AsSpan(0, marker.Index).IndexOfAny("()[]") >= 0)
            return "Unknown";

        return marker.Value.ToUpperInvariant() switch
        {
            "DEB" => "Debris",
            "R/B" => "Rocket Body",
            var value when value.StartsWith("R/B(", StringComparison.Ordinal) => "Rocket Body",
            // PLAT identifies a platform, not reliably one of our three categories.
            _ => "Unknown"
        };
    }

    // https://celestrak.org/satcat/satcat-format.php
    [GeneratedRegex(@"(?:\s+(?:\([^()]*\)|\[[^\[\]]*\]))+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingAnnotations();

    [GeneratedRegex(@"(?<!\S)(?:DEB|R/B(?:\([1-9][0-9]*\))?|PLAT)(?!\S)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeMarkers();
}
