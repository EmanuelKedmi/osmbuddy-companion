using System.Text.RegularExpressions;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// The item name is the first title line that is not a stat. A catalog match
/// uses the catalog spelling. Anything else is kept so the card can say the
/// price is unknown.
/// </summary>
public static partial class TooltipName
{
    public static string? FromLines(IReadOnlyList<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = Upgrades().Replace(raw.Trim(), "");
            if (line.Length < 2 || line.StartsWith('(')) continue;
            if (StatLine().IsMatch(line)) continue;
            if (PriceCatalog.TryRecover(line, out var canonical) && canonical.Length > 0) return canonical;
            return line;
        }

        return null;
    }

    /// <summary>
    /// A stat row, not an item whose name starts with the same word.
    /// "DEX : +5" and "Weapon Attack +3" are stats. "DEX Crystal Ore" and
    /// "Speed Potion" are items.
    /// </summary>
    [GeneratedRegex(
        @"^(?:(?:STR|DEX|INT|LUK|HP|MP|Max ?HP|Max ?MP|Weapon Attack|Magic Attack|Weapon Def|Magic Def|Accuracy|Avoidability|Speed|Jump)\s*[:.]?\s*[+\-]?\d|(?:Req(?:uired)?(?:\s+(?:Level|LEV|STR|DEX|INT|LUK))?|Number of upgrades|Upgrades available|Attack Speed|Category|Item Lev(?:el)?|Item EXP)\b.*[:.].+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatLine();

    [GeneratedRegex(@"\s*\(\+\d+\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Upgrades();
}
