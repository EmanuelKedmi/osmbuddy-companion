using System.Text.Json;
using System.Text.RegularExpressions;
using MarketNotes.Companion.Data;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Hunting-map names from catalog.json. The minimap paints the town above
/// the map name; only the map name is in this book.
/// </summary>
public static class MapCatalog
{
    private static readonly object Gate = new();
    private static Dictionary<string, (int Id, string Name)>? _byName;

    public static (int Id, string Name)? Match(string? text)
    {
        var key = Norm(text);
        if (key.Length == 0) return null;
        var names = Names();
        if (names.TryGetValue(key, out var exact)) return exact;

        (int Id, string Name)? best = null;
        var bestLength = 0;
        foreach (var pair in names)
        {
            if (pair.Key.Length < 8) continue;
            if (!key.Contains(pair.Key, StringComparison.Ordinal)) continue;
            if (pair.Key.Length > bestLength)
            {
                bestLength = pair.Key.Length;
                best = pair.Value;
            }
        }

        return best ?? Near(key, names);
    }

    /// <summary>
    /// OCR drops or splits a letter or two in the title ("Whirlpo I of Time",
    /// "Whirlpoo f Time"). Letters only are compared, about one edit per seven
    /// letters, and only one map may be that close. The closing numeral must
    /// match exactly, because Forest I and Forest II are different maps.
    /// </summary>
    private static (int Id, string Name)? Near(string key, Dictionary<string, (int Id, string Name)> names)
    {
        var letters = Letters(key);
        if (letters.Length < 10) return null;
        var numeral = Numeral(key);
        (int Id, string Name)? best = null;
        var bestDistance = int.MaxValue;
        var tied = false;
        foreach (var pair in names)
        {
            var other = Letters(pair.Key);
            if (other.Length < 10) continue;
            var allowed = Math.Max(1, other.Length / 7);
            if (Math.Abs(other.Length - letters.Length) > allowed) continue;
            if (!string.Equals(Numeral(pair.Key), numeral, StringComparison.Ordinal)) continue;
            var distance = EditDistance(letters, other, allowed);
            if (distance > allowed) continue;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = pair.Value;
                tied = false;
            }
            else if (distance == bestDistance && best?.Id != pair.Value.Id)
            {
                tied = true;
            }
        }

        return tied ? null : best;
    }

    private static string Letters(string key) => Regex.Replace(key, "[^a-z0-9]", "");

    private static string Numeral(string key)
    {
        var match = Regex.Match(key, @"\s([ivx]{1,4}|\d{1,3})$");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static int EditDistance(string left, string right, int cap)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                if (current[j] < rowMin) rowMin = current[j];
            }

            if (rowMin > cap) return cap + 1;
            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static Dictionary<string, (int Id, string Name)> Names()
    {
        if (_byName != null) return _byName;
        lock (Gate)
        {
            if (_byName != null) return _byName;
            var found = new Dictionary<string, (int Id, string Name)>(StringComparer.Ordinal);
            try
            {
                var path = DataFiles.Find("catalog.json");
                if (path != null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("maps", out var maps))
                    {
                        foreach (var map in maps.EnumerateArray())
                        {
                            if (!map.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
                            var name = map.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                            var key = Norm(name);
                            if (key.Length == 0 || found.ContainsKey(key)) continue;
                            found[key] = (id, name!.Trim());
                        }
                    }
                }
            }
            catch
            {
                // No catalog. Map text can still be held, without an id.
            }

            _byName = found;
            return found;
        }
    }

    private static string Norm(string? text)
    {
        var value = Regex.Replace(text ?? "", @"\s+", " ").Trim().ToLowerInvariant();
        value = value.Replace("<", " ").Replace(">", " ");
        return Regex.Replace(value, @"\s+", " ").Trim();
    }
}
