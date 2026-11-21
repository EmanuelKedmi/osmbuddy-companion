using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using MarketNotes.Companion.Farm;

namespace MarketNotes.Companion;

/// <summary>
/// Loads short version notes from GitHub Releases, with CHANGELOG.md as fallback.
/// </summary>
public static class CompanionChangelog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private const string EmbeddedName = "MarketNotes.Companion.CHANGELOG.md";

    public static IReadOnlyList<ChangelogEntry> Embedded() => ChangelogNotes.Parse(ReadEmbedded());

    public static async Task<IReadOnlyList<ChangelogEntry>> LoadAsync()
    {
        var fallback = Embedded();
        var remote = await TryLoadFromGitHubAsync();
        return ChangelogNotes.Merge(remote, fallback);
    }

    private static async Task<IReadOnlyList<ChangelogEntry>> TryLoadFromGitHubAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WebsiteAddress.ReleasesApiUrl);
            request.Headers.UserAgent.ParseAdd("MarketNotes.Companion");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return Array.Empty<ChangelogEntry>();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ChangelogEntry>();

            var entries = new List<ChangelogEntry>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var tag = item.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
                var version = WebsiteAddress.VersionFromReleaseTag(tag);
                if (version == null) continue;

                var body = item.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
                var parsed = ChangelogNotes.Parse(string.IsNullOrWhiteSpace(body) ? $"## {version}" : body);
                if (parsed.Count == 0)
                    entries.Add(new ChangelogEntry(version, Array.Empty<string>()));
                else
                    entries.AddRange(parsed);
            }

            return Dedupe(entries);
        }
        catch
        {
            return Array.Empty<ChangelogEntry>();
        }
    }

    private static IReadOnlyList<ChangelogEntry> Dedupe(IReadOnlyList<ChangelogEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ChangelogEntry>();
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Version)) continue;
            list.Add(entry);
        }

        return list;
    }

    private static string? ReadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
