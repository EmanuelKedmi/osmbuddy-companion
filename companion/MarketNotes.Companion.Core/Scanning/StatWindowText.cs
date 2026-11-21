using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Scanning;

public sealed class StatSheet
{
    public string? Name { get; init; }
    public string? Branch { get; init; }
    public int? Level { get; init; }
    public int? Str { get; init; }
    public int? Dex { get; init; }
    public int? Int { get; init; }
    public int? Luk { get; init; }
    public int? MaxHp { get; init; }
    /// <summary>Read for the status line. The character sheet does not store MP.</summary>
    public int? MaxMp { get; init; }
    public int? WeaponAttack { get; init; }
    public int? MagicAttack { get; init; }
    public int? Accuracy { get; init; }
    public int? WeaponDef { get; init; }
    public int? MagicDef { get; init; }
    public bool Succeeded { get; init; }

    public string Signature(string? name)
    {
        return string.Join('|', new string?[]
        {
            name ?? "",
            Branch,
            Level?.ToString(CultureInfo.InvariantCulture),
            Str?.ToString(CultureInfo.InvariantCulture),
            Dex?.ToString(CultureInfo.InvariantCulture),
            Int?.ToString(CultureInfo.InvariantCulture),
            Luk?.ToString(CultureInfo.InvariantCulture),
            MaxHp?.ToString(CultureInfo.InvariantCulture),
            WeaponAttack?.ToString(CultureInfo.InvariantCulture),
            MagicAttack?.ToString(CultureInfo.InvariantCulture),
            Accuracy?.ToString(CultureInfo.InvariantCulture),
            WeaponDef?.ToString(CultureInfo.InvariantCulture),
            MagicDef?.ToString(CultureInfo.InvariantCulture),
        });
    }
}

/// <summary>
/// Reads the stat window text. A line that is not a labeled number is left
/// unread. This does not fill a missing stat with a guess.
/// </summary>
public static class StatWindowText
{
    private const int MaxText = 4000;

    private static readonly (string Name, string Branch)[] Jobs =
    [
        ("arch mage (fire, poison)", "magician"),
        ("arch mage (ice, lightning)", "magician"),
        ("mage (fire, poison)", "magician"),
        ("mage (ice, lightning)", "magician"),
        ("wizard (fire, poison)", "magician"),
        ("wizard (ice, lightning)", "magician"),
        ("fire/poison arch mage", "magician"),
        ("ice/lightning arch mage", "magician"),
        ("fire/poison mage", "magician"),
        ("ice/lightning mage", "magician"),
        ("fire/poison wizard", "magician"),
        ("ice/lightning wizard", "magician"),
        ("bow master", "bowman"),
        ("marksman", "bowman"),
        ("night lord", "thief"),
        ("shadower", "thief"),
        ("dark knight", "warrior"),
        ("chief bandit", "thief"),
        ("dragon knight", "warrior"),
        ("white knight", "warrior"),
        ("crossbowman", "bowman"),
        ("gunslinger", "pirate"),
        ("buccaneer", "pirate"),
        ("corsair", "pirate"),
        ("marauder", "pirate"),
        ("outlaw", "pirate"),
        ("brawler", "pirate"),
        ("assassin", "thief"),
        ("bandit", "thief"),
        ("hermit", "thief"),
        ("rogue", "thief"),
        ("sniper", "bowman"),
        ("ranger", "bowman"),
        ("hunter", "bowman"),
        ("archer", "bowman"),
        ("bishop", "magician"),
        ("priest", "magician"),
        ("cleric", "magician"),
        ("wizard", "magician"),
        ("mage", "magician"),
        ("magician", "magician"),
        ("beginner", "beginner"),
        ("warrior", "warrior"),
        ("bowman", "bowman"),
        ("thief", "thief"),
        ("pirate", "pirate"),
        ("spearman", "warrior"),
        ("crusader", "warrior"),
        ("fighter", "warrior"),
        ("swordman", "warrior"),
        ("swordsman", "warrior"),
        ("paladin", "warrior"),
        ("hero", "warrior"),
        ("page", "warrior"),
        ("knight", "warrior"),
    ];

    public static StatSheet Parse(string? text)
    {
        var source = (text ?? "").Replace("\r", "");
        if (source.Length > MaxText) source = source[..MaxText];
        var lines = source
            .Split('\n')
            .Select(ScreenLabel.Normalize)
            .Where(line => line.Length > 0)
            .ToArray();

        var jobIndex = FindJob(lines, out var branch);
        var level = ReadLevel(lines);
        var str = LabeledInt(lines, @"^STR\b", 4, 9999);
        var dex = LabeledInt(lines, @"^DEX\b", 4, 9999);
        var intStat = LabeledInt(lines, @"^INT\b", 4, 9999);
        var luk = LabeledInt(lines, @"^LUK\b", 4, 9999);
        // OSM Worlds CHARACTER STAT: OCR often drops STR/DEX labels and leaves
        // "74 (61+13)" / "403 (361+42)" in order under the job and level.
        ApplyBonusTotals(lines, ref str, ref dex, ref intStat, ref luk);
        if (level == null) level = BareLevelAfterJob(lines, jobIndex);
        var maxHp = PairMax(lines, "HP", 1, 30000) ?? BarePairMax(lines, 1, 30000, firstPair: true);
        var maxMp = PairMax(lines, "MP", 0, 30000) ?? BarePairMax(lines, 0, 30000, firstPair: false);
        var weaponAttack = LabeledInt(lines, @"^(?:weapon\s+attack|weapon\s+att\.?|watk|w\.?\s*atk)\b", 0, 9999)
            ?? BareAttack(lines);
        var magicAttack = MagicAttack(lines);
        var accuracy = Accuracy(lines);
        var weaponDef = LabeledInt(lines, @"^(?:weapon\s+def(?:ense)?\.?|w\.?\s*def)\b", 0, 9999);
        var magicDef = LabeledInt(lines, @"^(?:magic\s+def(?:ense)?\.?|m\.?\s*def)\b", 0, 9999);
        ApplyCombatBlock(lines, ref weaponAttack, ref weaponDef, ref magicAttack, ref magicDef, ref accuracy);
        var name = FindName(lines, jobIndex);
        var title = lines.Any(IsStatTitle);
        var primary = Count(str, dex, intStat, luk);
        var signals = primary;
        if (level != null) signals++;
        if (maxHp != null) signals++;
        if (branch != null) signals++;
        if (weaponAttack != null) signals++;
        if (magicAttack != null) signals++;
        if (accuracy != null) signals++;
        var succeeded = signals >= 3 || (title && signals >= 2);

        return new StatSheet
        {
            Name = name,
            Branch = branch,
            Level = level,
            Str = str,
            Dex = dex,
            Int = intStat,
            Luk = luk,
            MaxHp = maxHp,
            MaxMp = maxMp,
            WeaponAttack = weaponAttack,
            MagicAttack = magicAttack,
            Accuracy = accuracy,
            WeaponDef = weaponDef,
            MagicDef = magicDef,
            Succeeded = succeeded,
        };
    }

    private static bool IsStatTitle(string line)
    {
        if (line.Equals("Stat", StringComparison.OrdinalIgnoreCase)
            || line.Equals("Stats", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // OCR of "CHARACTER STAT" often becomes CHRRRCTER STRT / CHARACTFR STAT.
        var key = Regex.Replace(line.ToLowerInvariant(), @"[^a-z]", "");
        return key.Contains("characterstat", StringComparison.Ordinal)
            || key.Contains("charrcterstrt", StringComparison.Ordinal)
            || key.Contains("chrrrcterstrt", StringComparison.Ordinal)
            || (key.Contains("character", StringComparison.Ordinal) && key.Contains("stat", StringComparison.Ordinal));
    }

    /// <summary>
    /// "total (base+bonus)" lines map to STR, DEX, INT, LUK in order.
    /// Worlds OCR often drops the LUK line (or collapses identical INT/LUK
    /// totals), so three totals still fill STR/DEX/INT.
    /// </summary>
    private static void ApplyBonusTotals(string[] lines, ref int? str, ref int? dex, ref int? intStat, ref int? luk)
    {
        if (str != null && dex != null && intStat != null && luk != null) return;
        var totals = new List<int>();
        foreach (var line in lines)
        {
            // Allow trailing OCR junk ("13 (4+9) ISTRI") — LUK digits sometimes
            // sit on the same line as a mangled label.
            var match = Regex.Match(line, @"^(\d{1,4})\s*\(\s*\d+\s*\+\s*\d+\s*\)");
            if (!match.Success) continue;
            var total = ParseInt(match.Groups[1].Value);
            if (total == null || total < 4 || total > 9999) continue;
            totals.Add(total.Value);
        }

        if (totals.Count < 3) return;
        // Worlds sheets list STR→DEX→INT→LUK. Three is enough for the primaries.
        str ??= totals[0];
        dex ??= totals[1];
        intStat ??= totals[2];
        if (totals.Count >= 4) luk ??= totals[3];
    }

    private static int? BareLevelAfterJob(string[] lines, int jobIndex)
    {
        if (jobIndex < 0) return null;
        for (var i = jobIndex + 1; i < Math.Min(jobIndex + 4, lines.Length); i++)
        {
            var match = Regex.Match(lines[i], @"^(\d{1,3})$");
            if (!match.Success) continue;
            var level = InRange(ParseInt(match.Groups[1].Value), 1, 200);
            if (level != null) return level;
        }

        return null;
    }

    private static int? BarePairMax(string[] lines, int min, int max, bool firstPair)
    {
        var values = new List<int>();
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"([\d,]+)\s*/\s*([\d,]+)");
            if (!match.Success) continue;
            var value = InRange(ParseInt(match.Groups[2].Value), min, max);
            if (value == null) continue;
            values.Add(value.Value);
        }

        if (values.Count == 0) return null;
        // Worlds sheet: first cur/max is HP, second is MP.
        if (firstPair) return values[0];
        return values.Count > 1 ? values[1] : null;
    }

    /// <summary>
    /// Worlds CHARACTER STAT lists combat labels then values:
    /// ATTACK / DEF / MAGIC / MAGIC DEF / ACCURACY / JUMP → 151 18 27 287 …
    /// OCR often mangles ACCURACY to RCCURRC'f and ATTACK to RTTRCK.
    /// </summary>
    private static void ApplyCombatBlock(
        string[] lines,
        ref int? weaponAttack,
        ref int? weaponDef,
        ref int? magicAttack,
        ref int? magicDef,
        ref int? accuracy)
    {
        var labelKinds = new List<string>();
        var values = new List<int>();
        var inLabels = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var kind = CombatLabelKind(lines[i]);
            if (kind != null)
            {
                inLabels = true;
                // JUMP/SPEED are present as labels but we do not store them; skip
                // so they do not shift ACCURACY onto the wrong number.
                if (kind != "skip") labelKinds.Add(kind);
                continue;
            }

            if (!inLabels) continue;
            if (Regex.IsMatch(lines[i], @"%")) break;
            var match = Regex.Match(lines[i], @"^(\d{1,4})$");
            if (!match.Success)
            {
                if (values.Count > 0) break;
                continue;
            }

            var parsed = ParseInt(match.Groups[1].Value);
            if (parsed == null) continue;
            values.Add(parsed.Value);
        }

        // OCR often emits MAGIC then MAGIC DEF as two labels for one MAGIC column.
        for (var i = 0; i < labelKinds.Count - 1; i++)
        {
            if (labelKinds[i] == "ma" && labelKinds[i + 1] == "md")
            {
                labelKinds.RemoveAt(i + 1);
                break;
            }
        }

        if (labelKinds.Count == 0 || values.Count == 0) return;
        var n = Math.Min(labelKinds.Count, values.Count);
        for (var i = 0; i < n; i++)
        {
            switch (labelKinds[i])
            {
                case "wa":
                    weaponAttack ??= values[i];
                    break;
                case "wd":
                    weaponDef ??= values[i];
                    break;
                case "ma":
                    magicAttack ??= values[i];
                    break;
                case "md":
                    magicDef ??= values[i];
                    break;
                case "acc":
                    accuracy ??= values[i];
                    break;
            }
        }
    }

    private static string? CombatLabelKind(string line)
    {
        var key = Regex.Replace(line.ToLowerInvariant(), @"[^a-z]", "");
        if (key.Length == 0) return null;
        if (key.Contains("magicdef", StringComparison.Ordinal) || key is "mdef" or "mrgicdef") return "md";
        if (key.Contains("accuracy", StringComparison.Ordinal) || key.Contains("rccurrc", StringComparison.Ordinal)
            || key is "acc" or "accurac") return "acc";
        if (key.Contains("magic", StringComparison.Ordinal) || key is "mrgic" or "matk") return "ma";
        if (key is "def" or "defense" or "wdef") return "wd";
        if (key.Contains("attack", StringComparison.Ordinal) || key is "rttrck" or "att" or "watk") return "wa";
        if (key is "jump" or "speed") return "skip";
        return null;
    }

    public static string Describe(StatSheet sheet, bool sample)
    {
        var lead = sample
            ? "Sample. OCR did not run. These stats were not read from a game window."
            : "Stat window.";
        if (!sheet.Succeeded)
        {
            return lead + " The text was not a readable stat sheet, so nothing was added.";
        }

        var parts = new List<string> { lead };
        if (sheet.Name != null) parts.Add(sheet.Name + ".");
        else parts.Add("The name was not on the window.");
        if (sheet.Branch != null) parts.Add("Job " + sheet.Branch + ".");
        if (sheet.Level != null) parts.Add("Level " + sheet.Level.Value.ToString(CultureInfo.InvariantCulture) + ".");
        else parts.Add("Level was not read.");
        var stats = new List<string>();
        if (sheet.Str != null) stats.Add("STR " + sheet.Str.Value.ToString(CultureInfo.InvariantCulture));
        if (sheet.Dex != null) stats.Add("DEX " + sheet.Dex.Value.ToString(CultureInfo.InvariantCulture));
        if (sheet.Int != null) stats.Add("INT " + sheet.Int.Value.ToString(CultureInfo.InvariantCulture));
        if (sheet.Luk != null) stats.Add("LUK " + sheet.Luk.Value.ToString(CultureInfo.InvariantCulture));
        if (stats.Count > 0) parts.Add(string.Join(", ", stats) + ".");
        if (sheet.MaxHp != null) parts.Add("Max HP " + sheet.MaxHp.Value.ToString(CultureInfo.InvariantCulture) + ".");
        if (sheet.MaxMp != null) parts.Add("Max MP " + sheet.MaxMp.Value.ToString(CultureInfo.InvariantCulture) + " was read and is not stored.");
        if (sheet.WeaponAttack != null) parts.Add("Weapon attack " + sheet.WeaponAttack.Value.ToString(CultureInfo.InvariantCulture) + ".");
        if (sheet.MagicAttack != null) parts.Add("Magic attack " + sheet.MagicAttack.Value.ToString(CultureInfo.InvariantCulture) + ".");
        if (sheet.Accuracy != null) parts.Add("Accuracy " + sheet.Accuracy.Value.ToString(CultureInfo.InvariantCulture) + ".");
        if (sheet.WeaponDef != null) parts.Add("Weapon def " + sheet.WeaponDef.Value.ToString(CultureInfo.InvariantCulture) + ".");
        if (sheet.MagicDef != null) parts.Add("Magic def " + sheet.MagicDef.Value.ToString(CultureInfo.InvariantCulture) + ".");
        return string.Join(" ", parts);
    }

    public static bool TryBranch(string? line, out string? branch)
    {
        branch = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var key = JobKey(line);
        foreach (var job in Jobs)
        {
            if (key != JobKey(job.Name)) continue;
            branch = job.Branch;
            return true;
        }

        return false;
    }

    private static int FindJob(string[] lines, out string? branch)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryBranch(lines[i], out branch)) continue;
            return i;
        }

        branch = null;
        return -1;
    }

    private static string JobKey(string line)
    {
        var text = line.ToLowerInvariant().Replace('/', ',');
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('.');
        return text;
    }

    private static string? FindName(string[] lines, int jobIndex)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < lines.Length && TakeName(lines[i + 1]) is string labeled) return labeled;
        }

        if (jobIndex > 0 && TakeName(lines[jobIndex - 1]) is string before) return before;
        if (jobIndex >= 0 && jobIndex + 1 < lines.Length && TakeName(lines[jobIndex + 1]) is string after) return after;

        // Worlds layout: chrome labels (JOB/LEVEL/GUILD/FAME) then name, then job.
        // When the job line was missed, still pick the first AcceptCharacter line —
        // but only on a CHARACTER STAT / Stat title so battle OCR cannot invent a name.
        if (!lines.Any(IsStatTitle)) return null;
        foreach (var line in lines)
        {
            if (IsChromeName(line) || IsStatTitle(line)) continue;
            if (TryBranch(line, out _)) continue;
            if (TakeName(line) is string loose) return loose;
        }

        return null;
    }

    private static string? TakeName(string line)
    {
        if (IsChromeName(line)) return null;
        return ScreenLabel.AcceptCharacter(line);
    }

    private static bool IsChromeName(string line)
    {
        var key = line.ToLowerInvariant();
        return key is "stat" or "stats" or "detail" or "skill" or "skills" or "name" or "job" or "ap" or "fame"
            or "frme" or "ffme" or "erp" or "exp" or "ok" or "close" or "hp" or "mp" or "str" or "dex" or "int"
            or "luk" or "level" or "lv" or "guild" or "ability point" or "abilitypoint" or "rbility point"
            or "rbilitypoint";
    }

    private static int? BareAttack(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("speed", StringComparison.OrdinalIgnoreCase)) continue;
            if (lines[i].Contains("magic", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Regex.IsMatch(lines[i], @"^(?:attack|att\.?)\b", RegexOptions.IgnoreCase)) continue;
            return NumberOn(lines, i, @"^(?:attack|att\.?)\b", 0, 9999);
        }

        return null;
    }

    private static int? MagicAttack(string[] lines)
    {
        var labeled = LabeledInt(lines, @"^(?:magic\s+attack|magic\s+att\.?|matk|m\.?\s*atk)\b", 0, 9999);
        if (labeled != null) return labeled;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^magic\b", RegexOptions.IgnoreCase)) continue;
            if (lines[i].Contains("def", StringComparison.OrdinalIgnoreCase)) continue;
            if (lines[i].Contains("attack", StringComparison.OrdinalIgnoreCase)) continue;
            return NumberOn(lines, i, @"^magic\b", 0, 9999);
        }

        return null;
    }

    private static int? Accuracy(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("avoid", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(lines[i], @"magic\s+accuracy", RegexOptions.IgnoreCase)) continue;
            if (!Regex.IsMatch(lines[i], @"^(?:accuracy|acc\.?)\b", RegexOptions.IgnoreCase)) continue;
            return NumberOn(lines, i, @"^(?:accuracy|acc\.?)\b", 0, 9999);
        }

        return null;
    }

    private static int? PairMax(string[] lines, string label, int min, int max)
    {
        var head = "^" + label + @"\b";
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], head, RegexOptions.IgnoreCase)) continue;
            var same = Pair(lines[i]);
            if (same != null) return InRange(same.Value, min, max);
            var single = Regex.Match(lines[i], head + @"\s*[:.]?\s*([\d,]+)\s*$", RegexOptions.IgnoreCase);
            if (single.Success) return InRange(ParseInt(single.Groups[1].Value), min, max);
            if (i + 1 < lines.Length)
            {
                var nextPair = Pair(lines[i + 1]);
                if (nextPair != null) return InRange(nextPair.Value, min, max);
                var next = Regex.Match(lines[i + 1], @"^([\d,]+)$");
                if (next.Success) return InRange(ParseInt(next.Groups[1].Value), min, max);
            }
        }

        return null;
    }

    private static int? Pair(string line)
    {
        var match = Regex.Match(line, @"([\d,]+)\s*/\s*([\d,]+)");
        if (!match.Success) return null;
        return ParseInt(match.Groups[2].Value);
    }

    /// <summary>
    /// Level is easy for OCR to mangle (LeveI, Lvl, Lv74). Prefer a clean
    /// label match, then a fuzzy Lv/Level token anywhere on the sheet.
    /// </summary>
    private static int? ReadLevel(string[] lines)
    {
        var clean = LabeledInt(lines, @"^(?:level|lv\.?|lvl)\b", 1, 200);
        if (clean != null) return clean;

        for (var i = 0; i < lines.Length; i++)
        {
            var same = Regex.Match(
                lines[i],
                @"(?i)\b(?:lv\.?|lvl|l[e3o]v[e3]?[l1!i]?)\s*[:.]?\s*(\d{1,3})\b");
            if (same.Success)
            {
                var value = InRange(ParseInt(same.Groups[1].Value), 1, 200);
                if (value != null) return value;
            }

            if (!Regex.IsMatch(lines[i], @"(?i)^(?:lv\.?|lvl|l[e3o]v)")) continue;
            if (i + 1 >= lines.Length) continue;
            var next = Regex.Match(lines[i + 1], @"^(\d{1,3})$");
            if (!next.Success) continue;
            var split = InRange(ParseInt(next.Groups[1].Value), 1, 200);
            if (split != null) return split;
        }

        return null;
    }

    private static int? LabeledInt(string[] lines, string label, int min, int max)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], label, RegexOptions.IgnoreCase)) continue;
            var found = NumberOn(lines, i, label, min, max);
            if (found != null) return found;
        }

        return null;
    }

    private static int? NumberOn(string[] lines, int index, string label, int min, int max)
    {
        var same = Regex.Match(lines[index], label + @"\s*[:.]?\s*([\d,]+)", RegexOptions.IgnoreCase);
        if (same.Success) return InRange(ParseInt(same.Groups[1].Value), min, max);
        if (index + 1 < lines.Length)
        {
            var next = Regex.Match(lines[index + 1], @"^([\d,]+)$");
            if (next.Success) return InRange(ParseInt(next.Groups[1].Value), min, max);
        }

        return null;
    }

    private static int? ParseInt(string raw)
    {
        if (!int.TryParse(raw.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return null;
        return parsed;
    }

    private static int? InRange(int? value, int min, int max)
    {
        if (value == null || value < min || value > max) return null;
        return value;
    }

    private static int Count(params int?[] values)
    {
        var count = 0;
        foreach (var value in values)
        {
            if (value != null) count++;
        }

        return count;
    }
}
