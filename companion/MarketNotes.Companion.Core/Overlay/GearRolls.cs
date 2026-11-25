using System.Text.Json;
using MarketNotes.Companion.Data;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// Min, average, and max for each stat on a dropped equip. The GMS v62
/// base is the average. A positive stat rolls evenly from base − span to
/// base + span. Span is the smaller of ceil(base / 10) and 5, or 10 for
/// defense, HP, and MP. A zero stat does not roll. Two different items that
/// share a name are left out.
/// </summary>
public static class GearRolls
{
    private static readonly object Gate = new();
    private static Dictionary<string, IReadOnlyList<GearRoll>>? _byName;

    private static readonly (string Key, string Label, int Cap)[] Stats =
    [
        ("pad", "Weapon Attack", 5),
        ("mad", "Magic Attack", 5),
        ("str", "STR", 5),
        ("dex", "DEX", 5),
        ("int", "INT", 5),
        ("luk", "LUK", 5),
        ("acc", "Accuracy", 5),
        ("eva", "Avoidability", 5),
        ("pdd", "Weapon Def", 10),
        ("mdd", "Magic Def", 10),
        ("hp", "HP", 10),
        ("mp", "MP", 10),
        ("speed", "Speed", 5),
        ("jump", "Jump", 5),
    ];

    public static int Span(int basis, int cap)
    {
        if (basis <= 0 || cap <= 0) return 0;
        return Math.Min((basis + 9) / 10, cap);
    }

    public static IReadOnlyList<GearRoll> For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var key = name.Trim();
        if (PriceCatalog.TryRecover(key, out var canonical) && canonical.Length > 0) key = canonical;
        Ensure();
        return _byName!.TryGetValue(key, out var rolls) ? rolls : [];
    }

    private static void Ensure()
    {
        if (_byName != null) return;
        lock (Gate)
        {
            if (_byName != null) return;
            var loaded = new Dictionary<string, IReadOnlyList<GearRoll>>(StringComparer.OrdinalIgnoreCase);
            var path = DataFiles.Find("equipment.json");
            if (path != null)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateObject())
                    {
                        var rolls = RollsOf(item.Value);
                        if (rolls.Count > 0) loaded[item.Name] = rolls;
                    }
                }
            }

            _byName = loaded;
        }
    }

    private static List<GearRoll> RollsOf(JsonElement item)
    {
        var rolls = new List<GearRoll>();
        foreach (var (key, label, cap) in Stats)
        {
            if (!item.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Number) continue;
            var basis = node.GetInt32();
            var span = Span(basis, cap);
            if (span == 0 && basis <= 0) continue;
            if (basis <= 0) continue;
            rolls.Add(new GearRoll(label, basis - span, basis, basis + span));
        }

        return rolls;
    }
}
