namespace MarketNotes.Companion.Farm;

/// <summary>
/// Where a companion copy sends shops and training. A Release build talks to
/// the public site. The local dev server is only the Debug default.
/// </summary>
public static class WebsiteAddress
{
    public const string LiveFeed = "https://osmbuddy.apps.grok.bot/api/farm/feed";
    public const string DevFeed = "http://127.0.0.1:4317/api/farm/feed";

    /// <summary>
    /// Releases live in the public companion repo. GitHub serves the executable.
    /// </summary>
    public const string ReleaseRepository = "EmanuelKedmi/osmbuddy-companion";
    public const string ReleaseAsset = "MarketNotes.Companion.exe";
    public const string ReleaseApiUrl = "https://api.github.com/repos/EmanuelKedmi/osmbuddy-companion/releases/latest";
    public const string ReleasesApiUrl = "https://api.github.com/repos/EmanuelKedmi/osmbuddy-companion/releases";
    public const string ReleaseDownloadUrl = "https://github.com/EmanuelKedmi/osmbuddy-companion/releases/latest/download/MarketNotes.Companion.exe";

    public static string LogsUrl(string? feedUrl) => Sibling(feedUrl, "/api/companion/logs");

    public static string VersionUrl(string? feedUrl) => Sibling(feedUrl, "/api/companion/version");

    public static string DownloadUrl(string? feedUrl) => Sibling(feedUrl, "/api/companion/download");

    private static string Sibling(string? feedUrl, string path)
    {
        if (!Uri.TryCreate((feedUrl ?? "").Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            uri = new Uri(LiveFeed);
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port)
        {
            Path = path,
        };
        return builder.Uri.AbsoluteUri;
    }

    public static bool IsLoopback(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    /// <summary>
    /// A GitHub release tag is <c>v1.2.3</c> or <c>1.2.3</c>. Other tags are ignored.
    /// </summary>
    public static string? VersionFromReleaseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1]))
            text = text[1..];
        var parts = text.Split('.');
        if (parts.Length != 3) return null;
        foreach (var part in parts)
        {
            if (part.Length == 0) return null;
            foreach (var c in part)
            {
                if (!char.IsDigit(c)) return null;
            }
        }

        return text;
    }
}
