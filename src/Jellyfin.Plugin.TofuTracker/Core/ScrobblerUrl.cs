namespace Jellyfin.Plugin.TofuTracker.Core;

/// <summary>The scrobbler base URL setting.</summary>
public static class ScrobblerUrl
{
    public const string Default = "https://scrobble.tofutracker.com";

    /// <summary>
    /// Validates and normalises a base URL (no trailing slash). Tokens travel in a header, so plain http
    /// is only accepted for loopback hosts, which is what a local test setup needs.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = Default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isLoopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>The configured URL, or the default when it is empty or invalid.</summary>
    public static string NormalizeOrDefault(string? input)
    {
        return TryNormalize(input, out var normalized) ? normalized : Default;
    }

    public static Uri Combine(string baseUrl, string path)
    {
        return new Uri(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute);
    }
}
