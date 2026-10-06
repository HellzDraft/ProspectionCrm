using System.Text.RegularExpressions;

namespace ProspectionCrm.Api.Services;

internal static partial class IngestionNormalization
{
    // Comparison only: business text is never rewritten with this key.
    internal static string TextKey(string value) => Spaces().Replace(value.Trim(), " ").ToUpperInvariant();

    // Do not use Uri.AbsoluteUri: it also rewrites paths/escapes/default ports.
    // Keep path, query, fragment, explicit port and escapes byte-for-byte.
    internal static string? UrlKey(string? value)
    {
        if (value is null) return null;
        var url = value.Trim();
        if (url.Length == 0 || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')) return null;
        var match = HttpUrl().Match(url);
        if (!match.Success || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || string.IsNullOrEmpty(parsed.Host) || parsed.UserInfo.Length != 0) return null;
        return match.Groups[1].Value.ToLowerInvariant() + "://"
            + match.Groups[2].Value.ToLowerInvariant() + match.Groups[3].Value;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\A(https?)://([^/?#]+)(.*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrl();
}
