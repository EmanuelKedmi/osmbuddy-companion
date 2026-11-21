using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketNotes.Companion.Data;

namespace MarketNotes.Companion.Scanning;

public readonly record struct PriceQuote(long? Meso, string Label)
{
    public static PriceQuote Missing { get; } = new(null, "Not in the database");
}

/// <summary>
/// Local shop prices. Discord sold, then Discord asks, then the classic
/// catalog, then the NPC price. The companion has no typed override.
/// Nothing here calls the website.
/// </summary>
public static class PriceCatalog
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static Dictionary<int, ItemRecord> _items = [];
    private static Dictionary<string, List<int>> _itemsByName = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _displayNames = new(StringComparer.Ordinal);
    private static Dictionary<int, DiscordRecord> _discord = [];
    private static Dictionary<string, List<int>> _discordByName = new(StringComparer.Ordinal);
    private static Dictionary<int, long> _classic = [];
    private readonly record struct LetterGroup(string Letters, string Name);

    private static List<LetterGroup> _letterGroups = [];

    private sealed record ItemRecord(int Id, string Name, long Price);
    private sealed record DiscordRecord(int Id, string Name, long Meso, string Source);

    public static string? LoadWarning { get; private set; }

    /// <summary>
    /// Exact catalog or Discord name, including a trailing skill level 1–30.
    /// Shop rows and drop lines both use this. A clipped name is not exact.
    /// </summary>
    public static bool TryExact(string itemName, out string canonical)
    {
        EnsureLoaded();
        canonical = "";
        var clean = Clean(itemName);
        if (clean.Length == 0) return false;
        if (TryCanonical(Norm(clean), out canonical)) return true;
        var stem = Stem(clean);
        return !stem.Equals(clean, StringComparison.OrdinalIgnoreCase) && TryCanonical(Norm(stem), out canonical);
    }

    /// <summary>
    /// The catalog, library, or Discord name for this text, when it is one of those items.
    /// A 20 or 30 mastery book keeps that level when the library name has it.
    /// A trailing level is ignored only when the leveled name is not itself an item.
    /// One wrong character still matches when that full name is unique and at least 8 characters.
    /// </summary>
    public static bool TryMatch(string itemName, out string canonical)
    {
        EnsureLoaded();
        canonical = "";
        var clean = Clean(itemName);
        if (clean.Length == 0) return false;
        if (TryExact(itemName, out canonical)) return true;
        if (TryFuzzy(clean, out canonical)) return true;
        var stem = Stem(clean);
        return !stem.Equals(clean, StringComparison.OrdinalIgnoreCase) && TryFuzzy(stem, out canonical);
    }

    /// <summary>
    /// The catalog name when this read is that item with the ending chopped
    /// off, or with a few letters missing. Two possible items is not a match.
    /// </summary>
    public static bool TryRecover(string name, out string canonical)
    {
        EnsureLoaded();
        if (TryMatch(name, out canonical)) return true;
        canonical = "";
        if (TryUniquePrefix(name, out canonical)) return true;
        if (TryMissingLetters(name, out canonical)) return true;
        canonical = "";
        return false;
    }

    public static PriceQuote Lookup(string itemName)
    {
        EnsureLoaded();
        var resolved = Resolve(itemName);
        if (resolved == null) return PriceQuote.Missing;
        if (_discord.TryGetValue(resolved.Id, out var discord) && discord.Meso >= 0)
        {
            var label = discord.Source.Equals("sold", StringComparison.OrdinalIgnoreCase)
                ? "Discord market · sold"
                : "Discord market · asks";
            return new PriceQuote(discord.Meso, label);
        }

        if (_classic.TryGetValue(resolved.Id, out var classic) && classic > 0)
        {
            return new PriceQuote(classic, "Classic");
        }

        if (resolved.Price > 0) return new PriceQuote(resolved.Price, "NPC");
        // The name is in the catalog. A zero NPC price is not the same as an unknown item.
        return new PriceQuote(null, "No listed price");
    }

    public static string GapText(long shopMeso, PriceQuote quote)
    {
        if (quote.Meso == null) return quote.Label;
        var delta = shopMeso - quote.Meso.Value;
        if (delta == 0) return "Same as the database";
        var amount = Math.Abs(delta).ToString("N0", CultureInfo.InvariantCulture);
        return delta < 0
            ? $"Shop is {amount} under the database"
            : $"Shop is {amount} over the database";
    }

    public static string CanonicalItemName(string name)
    {
        EnsureLoaded();
        var clean = Clean(name);
        if (clean.Equals("meso", StringComparison.OrdinalIgnoreCase) || clean.Equals("mesos", StringComparison.OrdinalIgnoreCase))
        {
            return "Mesos";
        }

        var key = Norm(clean);
        if (key.Length == 0) return clean;
        if (_displayNames.TryGetValue(key, out var exact)) return exact;
        if (key.Length > 2 && key.EndsWith('s') && _displayNames.TryGetValue(key[..^1], out var singular)) return singular;
        return clean;
    }

    private static ItemRecord? Resolve(string itemName)
    {
        if (!TryMatch(itemName, out var canonical)) return null;
        return FindRecord(Norm(canonical));
    }

    private static bool TryCanonical(string key, out string canonical)
    {
        canonical = "";
        if (key.Length == 0) return false;
        if (_displayNames.TryGetValue(key, out var shown) && shown.Length > 0)
        {
            canonical = shown;
            return true;
        }

        return false;
    }

    private static ItemRecord? FindRecord(string key)
    {
        if (_discordByName.TryGetValue(key, out var discordIds))
        {
            foreach (var id in discordIds)
            {
                if (_items.TryGetValue(id, out var fromDiscord)) return fromDiscord;
            }
        }

        if (_itemsByName.TryGetValue(key, out var catalogIds))
        {
            foreach (var id in catalogIds)
            {
                if (_items.TryGetValue(id, out var fromCatalog)) return fromCatalog;
            }
        }

        return null;
    }

    private static bool TryFuzzy(string name, out string canonical)
    {
        canonical = "";
        var key = Norm(name);
        if (key.Length < 8) return false;
        string? winner = null;
        foreach (var pair in _displayNames)
        {
            if (Math.Abs(pair.Key.Length - key.Length) > 1) continue;
            if (EditDistance(pair.Key, key) != 1) continue;
            if (winner != null && !winner.Equals(pair.Value, StringComparison.Ordinal)) return false;
            winner = pair.Value;
        }

        if (winner == null) return false;
        canonical = winner;
        return true;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var leftChar = left[i - 1];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = leftChar == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string Stem(string name)
    {
        var match = Regex.Match(name, @"^(?<base>.+?)\s+(?<level>\d{1,2})$", RegexOptions.CultureInvariant);
        if (!match.Success) return name;
        if (!int.TryParse(match.Groups["level"].Value, out var level) || level is < 1 or > 30) return name;
        var basis = match.Groups["base"].Value.Trim();
        return basis.Length < 4 ? name : basis;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            var items = new Dictionary<int, ItemRecord>();
            var itemsByName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var display = new Dictionary<string, string>(StringComparer.Ordinal);
            var discord = new Dictionary<int, DiscordRecord>();
            var discordByName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var classic = new Dictionary<int, long>();
            var warnings = new List<string>();

            var catalogPath = DataFiles.Find("catalog.json");
            if (catalogPath == null) warnings.Add("data/catalog.json was not found, so classic and NPC prices are missing.");
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
                    if (doc.RootElement.TryGetProperty("items", out var itemArray))
                    {
                        foreach (var item in itemArray.EnumerateArray())
                        {
                            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
                            var name = Clean(item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "");
                            long price = 0;
                            if (item.TryGetProperty("price", out var priceElement) && priceElement.TryGetInt64(out var parsedPrice)) price = parsedPrice;
                            if (name.Length == 0 || items.ContainsKey(id)) continue;
                            items[id] = new ItemRecord(id, name, price);
                            var key = Norm(name);
                            if (key.Length == 0) continue;
                            if (!itemsByName.TryGetValue(key, out var list))
                            {
                                list = [];
                                itemsByName[key] = list;
                            }
                            list.Add(id);
                            display.TryAdd(key, name);
                        }
                    }

                    if (doc.RootElement.TryGetProperty("scrolls", out var scrolls))
                    {
                        foreach (var scroll in scrolls.EnumerateArray())
                        {
                            if (!scroll.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
                            if (!scroll.TryGetProperty("meso", out var mesoElement) || !mesoElement.TryGetInt64(out var meso)) continue;
                            classic[id] = meso;
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"data/catalog.json could not be read ({ex.GetType().Name}).");
                }
            }

            var pricePath = DataFiles.Find("osm-market-prices.json");
            if (pricePath == null) warnings.Add("data/osm-market-prices.json was not found, so Discord prices are missing.");
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pricePath));
                    if (doc.RootElement.TryGetProperty("items", out var rows))
                    {
                        foreach (var row in rows.EnumerateArray())
                        {
                            if (!row.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
                            var name = Clean(row.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "");
                            if (!row.TryGetProperty("meso", out var mesoElement) || !mesoElement.TryGetInt64(out var meso)) continue;
                            var source = row.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() ?? "" : "";
                            discord[id] = new DiscordRecord(id, name, meso, source);
                            var key = Norm(name);
                            if (key.Length == 0) continue;
                            if (!discordByName.TryGetValue(key, out var list))
                            {
                                list = [];
                                discordByName[key] = list;
                            }
                            list.Add(id);
                            display.TryAdd(key, name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"data/osm-market-prices.json could not be read ({ex.GetType().Name}).");
                }
            }

            LoadAliases(items, itemsByName, display, warnings);

            _items = items;
            _itemsByName = itemsByName;
            _displayNames = display;
            _discord = discord;
            _discordByName = discordByName;
            _classic = classic;
            var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var shown in display.Values.Distinct(StringComparer.Ordinal))
            {
                var letters = Letters(shown);
                if (letters.Length < 8) continue;
                if (!grouped.TryGetValue(letters, out var names))
                {
                    names = [];
                    grouped[letters] = names;
                }

                if (!names.Contains(shown)) names.Add(shown);
            }

            _letterGroups = grouped
                .Where(pair => pair.Value.Count == 1)
                .Select(pair => new LetterGroup(pair.Key, pair.Value[0]))
                .ToList();
            LoadWarning = warnings.Count == 0 ? null : string.Join(" ", warnings);
            _loaded = true;
        }
    }

    /// <summary>
    /// Library names the GMS string omits, such as a mastery book's 20 or 30.
    /// A name the catalog or Discord list already uses is left alone.
    /// </summary>
    private static void LoadAliases(
        Dictionary<int, ItemRecord> items,
        Dictionary<string, List<int>> itemsByName,
        Dictionary<string, string> display,
        List<string> warnings)
    {
        var aliasPath = DataFiles.Find("item-aliases.json");
        if (aliasPath == null) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(aliasPath));
            if (!doc.RootElement.TryGetProperty("items", out var rows)) return;
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
                if (!items.ContainsKey(id)) continue;
                var name = Clean(row.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "");
                var key = Norm(name);
                if (key.Length == 0 || display.ContainsKey(key) || itemsByName.ContainsKey(key)) continue;
                display[key] = name;
                itemsByName[key] = [id];
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"data/item-aliases.json could not be read ({ex.GetType().Name}).");
        }
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static string Norm(string name) => Clean(name).ToLowerInvariant();

    private static bool TryUniquePrefix(string name, out string canonical)
    {
        canonical = "";
        var key = Norm(name);
        if (key.Length < 8) return false;
        string? winner = null;
        foreach (var pair in _displayNames)
        {
            var extra = pair.Key.Length - key.Length;
            if (extra is < 1 or > 6) continue;
            if (!pair.Key.StartsWith(key, StringComparison.Ordinal)) continue;
            if (pair.Key[key.Length..].Any(char.IsDigit)) continue;
            if (winner != null && !winner.Equals(pair.Value, StringComparison.Ordinal)) return false;
            winner = pair.Value;
        }

        if (winner == null) return false;
        canonical = winner;
        return true;
    }

    private static bool TryMissingLetters(string name, out string canonical)
    {
        canonical = "";
        var letters = Letters(name);
        if (letters.Length < 8) return false;
        string? winner = null;
        foreach (var group in _letterGroups)
        {
            var skipped = group.Letters.Length - letters.Length;
            var hit = skipped is >= 1 and <= 3 && IsSubsequence(letters, group.Letters)
                || skipped == 0 && EditDistance(letters, group.Letters) == 1;
            if (!hit) continue;
            if (winner != null && !winner.Equals(group.Name, StringComparison.Ordinal)) return false;
            winner = group.Name;
        }

        if (winner == null) return false;
        canonical = winner;
        return true;
    }

    private static string Letters(string name)
    {
        var chars = new char[name.Length];
        var count = 0;
        foreach (var ch in name)
        {
            if (!char.IsLetter(ch)) continue;
            chars[count++] = char.ToLowerInvariant(ch);
        }

        return new string(chars, 0, count);
    }

    private static bool IsSubsequence(string shorter, string longer)
    {
        var index = 0;
        foreach (var ch in longer)
        {
            if (index < shorter.Length && shorter[index] == ch) index++;
        }

        return index == shorter.Length;
    }
}
