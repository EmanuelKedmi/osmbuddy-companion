using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// What drops on a hunting map, from catalog.json. The same file feeds the
/// planner: a map's spawns point at mobs, and each mob's drop rows are
/// item id, minimum, maximum, and chance. Chance uses the catalog unit of
/// 1,000,000. Global drops, when the file lists any, drop on every map.
/// </summary>
public static class MapDrops
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static Dictionary<int, Dictionary<string, int>> _chanceByMap = [];
    private static Dictionary<string, int?> _mapIdByName = new(StringComparer.Ordinal);
    private static List<CatalogName> _names = [];

    private readonly record struct CatalogName(string Key, string Display);

    public static int? Resolve(int? mapId, string? mapName)
    {
        Ensure();
        if (mapId is int id && _chanceByMap.ContainsKey(id)) return id;
        var key = DropLines.NormalizeKey(mapName ?? "");
        if (key.Length == 0) return null;
        return _mapIdByName.TryGetValue(key, out var named) ? named : null;
    }

    public static bool TryChance(int mapId, string itemName, out int chance)
    {
        Ensure();
        chance = 0;
        if (!_chanceByMap.TryGetValue(mapId, out var items)) return false;
        return items.TryGetValue(DropLines.NormalizeKey(itemName), out chance);
    }

    /// <summary>
    /// Catalog names that start with this text and continue past it.
    /// When none do, a single wrong or extra character at the cut still counts
    /// if the text is at least 8 characters. That fallback is not used once a
    /// literal prefix already matches, so "DEX" does not also pull in "DEF".
    /// </summary>
    /// <summary>
    /// The one item that drops on this map within two letters of the text
    /// ("Cactus' Flouver" on a cactus map). Only this map's drop table is
    /// searched, so two edits cannot reach an unrelated catalog item.
    /// </summary>
    public static string? NearOnMap(int mapId, string text)
    {
        Ensure();
        if (!_chanceByMap.TryGetValue(mapId, out var items)) return null;
        var key = DropLines.NormalizeKey(text);
        if (key.Length < 8) return null;
        string? hit = null;
        foreach (var name in items.Keys)
        {
            if (Math.Abs(name.Length - key.Length) > 2) continue;
            if (EditDistance(name, key) > 2) continue;
            if (hit != null) return null;
            hit = name;
        }

        if (hit == null) return null;
        foreach (var name in _names)
        {
            if (name.Key == hit) return name.Display;
        }

        return null;
    }

    public static List<string> NamesStartingWith(string text)
    {
        Ensure();
        var key = PrefixKey(text);
        if (!Meaningful(key)) return [];
        var literal = Literal(key);
        if (literal.Count > 0 || key.Length < 8) return literal;
        // "Haunted Essence IJ": a one- or two-letter last word is a numeral
        // misread, and one edit there picks between I, II, and III.
        var lastWord = key[(key.LastIndexOf(' ') + 1)..];
        if (lastWord.Length <= 2) return [];

        var dirty = new List<string>();
        foreach (var name in _names)
        {
            if (name.Key.Length <= key.Length) continue;
            if (EditDistance(name.Key[..key.Length], key) != 1) continue;
            dirty.Add(name.Display);
        }

        return dirty;
    }

    private static List<string> Literal(string key)
    {
        var hits = new List<string>();
        foreach (var name in _names)
        {
            if (name.Key.Length > key.Length && name.Key.StartsWith(key, StringComparison.Ordinal))
                hits.Add(name.Display);
        }

        return hits;
    }

    private static bool Meaningful(string key)
    {
        if (key.Length >= 4) return true;
        return Regex.IsMatch(key, @"^\d{1,3}%$");
    }

    private static string PrefixKey(string text)
    {
        var key = DropLines.NormalizeKey(text);
        return Regex.Replace(key, @"[^a-z0-9%]+$", "");
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

    private static void Ensure()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            Load();
            _loaded = true;
        }
    }

    private static void Load()
    {
        var names = new List<CatalogName>();
        var nameById = new Dictionary<int, string>();
        var mobDrops = new Dictionary<int, List<(int ItemId, int Chance)>>();
        var globals = new List<(int ItemId, int Chance)>();
        var chanceByMap = new Dictionary<int, Dictionary<string, int>>();
        var mapIdByName = new Dictionary<string, int?>(StringComparer.Ordinal);

        try
        {
            var path = Data.DataFiles.Find("catalog.json");
            if (path == null)
            {
                Finish(names, chanceByMap, mapIdByName);
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
                    var display = Clean(item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null);
                    var key = DropLines.NormalizeKey(display);
                    if (key.Length == 0 || nameById.ContainsKey(id)) continue;
                    nameById[id] = display;
                    names.Add(new CatalogName(key, display));
                }
            }

            if (root.TryGetProperty("drops", out var drops))
            {
                foreach (var mob in drops.EnumerateObject())
                {
                    if (!int.TryParse(mob.Name, out var mobId)) continue;
                    mobDrops[mobId] = ReadRows(mob.Value);
                }
            }

            if (root.TryGetProperty("globalDrops", out var globalDrops))
                globals = ReadRows(globalDrops);

            if (root.TryGetProperty("maps", out var maps))
            {
                foreach (var map in maps.EnumerateArray())
                {
                    if (!map.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var mapId)) continue;
                    var mapName = DropLines.NormalizeKey(map.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "");
                    if (mapName.Length > 0)
                    {
                        if (mapIdByName.ContainsKey(mapName)) mapIdByName[mapName] = null;
                        else mapIdByName[mapName] = mapId;
                    }

                    var table = new Dictionary<string, int>(StringComparer.Ordinal);
                    if (map.TryGetProperty("spawns", out var spawns))
                    {
                        foreach (var spawn in spawns.EnumerateArray())
                        {
                            if (spawn.GetArrayLength() < 1 || !spawn[0].TryGetInt32(out var mobId)) continue;
                            if (!mobDrops.TryGetValue(mobId, out var rows)) continue;
                            AddRows(table, rows, nameById);
                        }
                    }

                    AddRows(table, globals, nameById);
                    chanceByMap[mapId] = table;
                }
            }
        }
        catch
        {
            // No drop table. Exact catalog names can still be confirmed.
        }

        Finish(names, chanceByMap, mapIdByName);
    }

    private static void Finish(
        List<CatalogName> names,
        Dictionary<int, Dictionary<string, int>> chanceByMap,
        Dictionary<string, int?> mapIdByName)
    {
        _names = names;
        _chanceByMap = chanceByMap;
        _mapIdByName = mapIdByName;
    }

    private static void AddRows(
        Dictionary<string, int> table,
        List<(int ItemId, int Chance)> rows,
        Dictionary<int, string> nameById)
    {
        foreach (var (itemId, chance) in rows)
        {
            if (!nameById.TryGetValue(itemId, out var display)) continue;
            var key = DropLines.NormalizeKey(display);
            if (key.Length == 0) continue;
            if (!table.TryGetValue(key, out var prior) || chance > prior) table[key] = chance;
        }
    }

    private static List<(int ItemId, int Chance)> ReadRows(JsonElement rows)
    {
        var found = new List<(int, int)>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4) continue;
            if (!row[0].TryGetInt32(out var itemId) || itemId <= 0) continue;
            if (!TryChanceValue(row[3], out var chance) || chance <= 0) continue;
            found.Add((itemId, chance));
        }

        return found;
    }

    private static bool TryChanceValue(JsonElement element, out int chance)
    {
        if (element.TryGetInt32(out chance)) return true;
        if (element.TryGetDouble(out var number) && number >= 0 && number <= int.MaxValue)
        {
            chance = (int)number;
            return true;
        }

        chance = 0;
        return false;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }
}
