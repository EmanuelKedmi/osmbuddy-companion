using System.Text;
using System.Text.RegularExpressions;

namespace MarketNotes.Companion;

/// <summary>
/// Short per-version headlines for the companion changelog window.
/// </summary>
public sealed record ChangelogEntry(string Version, IReadOnlyList<string> Headlines);

/// <summary>
/// Turns a CHANGELOG file or semantic-release GitHub body into version blocks.
/// </summary>
public static class ChangelogNotes
{
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex CommitSuffix = new(@"\s*\(([0-9a-f]{7,40})\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionHeading = new(@"^#{1,3}\s*(?:\[)?v?(\d+\.\d+\.\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<ChangelogEntry> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ChangelogEntry>();

        var entries = new List<ChangelogEntry>();
        string? version = null;
        var headlines = new List<string>();

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var heading = VersionHeading.Match(line);
            if (heading.Success)
            {
                Flush(entries, version, headlines);
                version = heading.Groups[1].Value;
                headlines.Clear();
                continue;
            }

            if (version == null) continue;
            if (line.StartsWith("### ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;

            var bullet = line;
            if (bullet.StartsWith("* ", StringComparison.Ordinal) || bullet.StartsWith("- ", StringComparison.Ordinal))
                bullet = bullet[2..].Trim();
            else
                continue;

            var headline = CleanHeadline(bullet);
            if (headline.Length == 0) continue;
            if (!headlines.Contains(headline, StringComparer.OrdinalIgnoreCase))
                headlines.Add(headline);
        }

        Flush(entries, version, headlines);
        return entries;
    }

    public static IReadOnlyList<ChangelogEntry> Merge(
        IReadOnlyList<ChangelogEntry> preferred,
        IReadOnlyList<ChangelogEntry> fallback)
    {
        if (preferred.Count == 0) return fallback;
        if (fallback.Count == 0) return preferred;

        var byVersion = new Dictionary<string, ChangelogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in fallback)
            byVersion[entry.Version] = entry;

        var order = new List<string>();
        foreach (var entry in preferred)
        {
            if (entry.Headlines.Count > 0)
                byVersion[entry.Version] = entry;
            else if (!byVersion.ContainsKey(entry.Version))
                byVersion[entry.Version] = entry;

            if (!order.Contains(entry.Version, StringComparer.OrdinalIgnoreCase))
                order.Add(entry.Version);
        }

        foreach (var entry in fallback)
        {
            if (!order.Contains(entry.Version, StringComparer.OrdinalIgnoreCase))
                order.Add(entry.Version);
        }

        var merged = new List<ChangelogEntry>(order.Count);
        foreach (var version in order)
        {
            if (byVersion.TryGetValue(version, out var entry))
                merged.Add(entry);
        }

        return merged;
    }

    public static string FormatPlain(IReadOnlyList<ChangelogEntry> entries)
    {
        if (entries.Count == 0) return "No changelog notes yet.";
        var sb = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i > 0) sb.AppendLine();
            sb.AppendLine(entry.Version);
            if (entry.Headlines.Count == 0)
            {
                sb.AppendLine("· (no notes)");
                continue;
            }

            foreach (var headline in entry.Headlines)
                sb.AppendLine("· " + headline);
        }

        return sb.ToString().TrimEnd();
    }

    private static void Flush(List<ChangelogEntry> entries, string? version, List<string> headlines)
    {
        if (version == null) return;
        entries.Add(new ChangelogEntry(version, headlines.ToArray()));
    }

    private static string CleanHeadline(string text)
    {
        var cleaned = MarkdownLink.Replace(text.Trim(), "$1");
        cleaned = CommitSuffix.Replace(cleaned, "").Trim().TrimEnd('.');
        if (cleaned.Length == 0) return "";
        if (char.IsLetter(cleaned[0]) && char.IsLower(cleaned[0]))
            cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
        return cleaned + ".";
    }
}
