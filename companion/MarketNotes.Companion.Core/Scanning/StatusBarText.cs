using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Level and HP/MP on the always-on bottom status plate (LV 74, 1602/2637, …).
/// Used when the Stat window is closed so level and max HP still sync.
/// </summary>
public sealed class StatusBarSheet
{
    public int? Level { get; init; }
    public string? Branch { get; init; }
    public int? MaxHp { get; init; }
    public int? MaxMp { get; init; }
    public string? Name { get; init; }
    public bool Succeeded => Level != null || MaxHp != null;

    // Omit MaxMp: OCR often mangles the MP pool and would re-POST every tick.
    public string Signature() => string.Join("|",
        Level?.ToString(CultureInfo.InvariantCulture),
        MaxHp?.ToString(CultureInfo.InvariantCulture),
        Branch,
        Name);
}

public static class StatusBarText
{
    public static StatusBarSheet Parse(string? text)
    {
        var source = (text ?? "").Replace("\r", "");
        var lines = source
            .Split('\n')
            .Select(ScreenLabel.Normalize)
            .Where(line => line.Length > 0)
            .ToArray();
        var joined = string.Join(" ", lines);

        var level = ReadLevel(joined) ?? ReadLevel(string.Join("\n", lines));
        var hp = PairMax(joined, "HP") ?? PairMaxLoose(joined, isHp: true);
        var mp = PairMax(joined, "MP") ?? PairMaxLoose(joined, isHp: false);
        string? branch = null;
        foreach (var line in lines)
        {
            if (StatWindowText.TryBranch(line, out var b))
            {
                branch = b;
                break;
            }
        }

        // Job often sits on the same line as LV: "LV 74 Sniper"
        if (branch == null)
        {
            var job = Regex.Match(joined, @"(?i)\bLV\s*\.?\s*\d{1,3}\s+([A-Za-z][A-Za-z /,]{2,24})");
            if (job.Success && StatWindowText.TryBranch(job.Groups[1].Value, out var fromLv))
            {
                branch = fromLv;
            }
        }

        // Do not AcceptCharacter on the whole plate: chat chrome ("To All") wins.
        // The name band / Stat window supply the character name.

        return new StatusBarSheet
        {
            Level = level,
            Branch = branch,
            MaxHp = hp,
            MaxMp = mp,
            Name = null,
        };
    }

    public static string Describe(StatusBarSheet sheet)
    {
        if (!sheet.Succeeded) return "Status plate: level and HP were not read.";
        var parts = new List<string> { "Status plate." };
        if (sheet.Name != null) parts.Add(sheet.Name + ".");
        if (sheet.Branch != null) parts.Add("Job " + sheet.Branch + ".");
        if (sheet.Level != null) parts.Add("Level " + sheet.Level.Value.ToString(CultureInfo.InvariantCulture) + ".");
        if (sheet.MaxHp != null) parts.Add("Max HP " + sheet.MaxHp.Value.ToString(CultureInfo.InvariantCulture) + ".");
        if (sheet.MaxMp != null) parts.Add("Max MP " + sheet.MaxMp.Value.ToString(CultureInfo.InvariantCulture) + " was read and is not stored.");
        return string.Join(" ", parts);
    }

    private static int? ReadLevel(string text)
    {
        var match = Regex.Match(text, @"(?i)\bLV\s*\.?\s*(\d{1,3})\b");
        if (match.Success) return InRange(ParseInt(match.Groups[1].Value), 1, 200);
        match = Regex.Match(text, @"(?i)\b(?:lv\.?|lvl|l[e3o]v[e3]?[l1!i]?)\s*[:.]?\s*(\d{1,3})\b");
        if (match.Success) return InRange(ParseInt(match.Groups[1].Value), 1, 200);
        // Dedicated LV-badge crop often returns only the digits.
        match = Regex.Match(text.Trim(), @"^(\d{1,3})$");
        if (match.Success) return InRange(ParseInt(match.Groups[1].Value), 1, 200);
        return null;
    }

    private static int? PairMax(string text, string label)
    {
        var match = Regex.Match(text, $@"(?i)\b{label}\b[^0-9]{{0,8}}([\d,]+)\s*/\s*([\d,]+)");
        if (!match.Success) return null;
        return InRange(ParseInt(match.Groups[2].Value), label.Equals("HP", StringComparison.OrdinalIgnoreCase) ? 1 : 0, 30000);
    }

    /// <summary>Bare cur/max pairs near the bars when the HP/MP label was missed.</summary>
    private static int? PairMaxLoose(string text, bool isHp)
    {
        var matches = Regex.Matches(text, @"([\d,]+)\s*/\s*([\d,]+)");
        if (matches.Count == 0) return null;
        // First pair is usually HP, second MP.
        var pick = isHp ? matches[0] : (matches.Count > 1 ? matches[1] : null);
        if (pick == null) return null;
        return InRange(ParseInt(pick.Groups[2].Value), isHp ? 1 : 0, 30000);
    }

    private static int? ParseInt(string raw)
    {
        if (!int.TryParse(raw.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        return parsed;
    }

    private static int? InRange(int? value, int min, int max)
    {
        if (value == null || value < min || value > max) return null;
        return value;
    }
}
