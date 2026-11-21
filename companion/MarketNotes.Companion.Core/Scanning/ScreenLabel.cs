using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// <paramref name="StreetOnly"/>: the band had a map line under the street
/// line, and only the street line ("Ludibrium" above "Whirlpool of Time")
/// matched a catalog map.
/// </summary>
public readonly record struct MapChoice(string Name, int? MapId, bool FromCatalog, bool StreetOnly = false);

/// <summary>
/// Keeps the last name that looked like a real label. A new name needs the
/// same read twice in a row, so one bad OCR frame does not replace it. A read
/// that is only part of the held name (a cursor over "ExtraSin" leaves
/// "Extra" or "aSin") never replaces it.
/// </summary>
public sealed class LabelHold
{
    /// <summary>How long a different name without its job line must keep reading the same before it replaces a held name.</summary>
    public const long WeakReplaceMs = 30_000;

    private readonly Func<string?, string?> _accept;
    private readonly Func<string?, bool>? _whole;
    private string? _pending;
    private long _pendingSince;

    /// <param name="whole">The read shows the whole plate (the job line too), so a different name can replace the held one after two reads.</param>
    public LabelHold(Func<string?, string?> accept, Func<string?, bool>? whole = null)
    {
        _accept = accept;
        _whole = whole;
    }

    public string Value { get; private set; } = "";

    /// <summary>A name is held and no different read is waiting to confirm.</summary>
    public bool Settled => Value.Length > 0 && _pending == null;

    public bool Observe(string? raw) => Observe(raw, Environment.TickCount64);

    public bool Observe(string? raw, long nowMs)
    {
        var next = _accept(raw);
        if (string.IsNullOrWhiteSpace(next)) return false;
        if (next.Equals(Value, StringComparison.OrdinalIgnoreCase))
        {
            _pending = null;
            return false;
        }

        if (Value.Length > 0 && Value.Contains(next, StringComparison.OrdinalIgnoreCase))
        {
            _pending = null;
            return false;
        }

        // OCR often drops one letter (MonkeDKof vs MonkeyDKof). Keep the
        // longer held name; only upgrade when the new read is longer.
        if (Value.Length > 0 && ScreenLabel.NamesClose(Value, next))
        {
            _pending = null;
            if (next.Length <= Value.Length) return false;
            Value = next;
            return true;
        }

        if (!next.Equals(_pending, StringComparison.OrdinalIgnoreCase))
        {
            _pending = next;
            _pendingSince = nowMs;
            return false;
        }

        var whole = _whole?.Invoke(raw) ?? true;
        if (Value.Length > 0 && !whole && nowMs - _pendingSince < WeakReplaceMs) return false;

        Value = next;
        _pending = null;
        return true;
    }
}

/// <summary>
/// Map title from the minimap band. A catalog map name wins over the town
/// line painted above it. A catalog lock is not replaced by the town line
/// or by one weak read.
/// </summary>
public sealed class MapHold
{
    private string? _pending;
    private bool _streetOnly;
    private bool _doubt;

    public string Value { get; private set; } = "";
    public int? MapId { get; private set; }
    public bool FromCatalog { get; private set; }

    /// <summary>
    /// A catalog map from its own line is held, and the last read agreed.
    /// While false the title band is worth reading again.
    /// </summary>
    public bool Settled => FromCatalog && !_streetOnly && !_doubt && _pending == null;

    public bool Observe(MapChoice? choice)
    {
        if (choice == null || string.IsNullOrWhiteSpace(choice.Value.Name)) return false;
        var next = choice.Value;
        if (Value.Length > 0 && ScreenLabel.SamePlace(Value, next.Name))
        {
            if (next.MapId != null) MapId = next.MapId;
            if (next.FromCatalog) Value = next.Name.Trim();
            if (next.FromCatalog && !next.StreetOnly) _streetOnly = false;
            _pending = null;
            _doubt = false;
            return false;
        }

        if (FromCatalog && (!next.FromCatalog || next.StreetOnly))
        {
            _doubt = true;
            return false;
        }

        // Free Market rooms are not in the hunting catalog. Do not let a
        // two-frame OCR glitch ("GoC") replace a held Free Market name.
        if (Value.Length > 0
            && FmMapGate.Classify(Value).Ok
            && !next.FromCatalog
            && !FmMapGate.Classify(next.Name).Ok)
        {
            _doubt = true;
            _pending = null;
            return false;
        }

        if (Value.Length == 0 && (!next.FromCatalog || next.StreetOnly))
        {
            if (string.Equals(_pending, next.Name, StringComparison.OrdinalIgnoreCase))
            {
                Assign(next);
                return true;
            }

            _pending = next.Name.Trim();
            return false;
        }

        if (Value.Length == 0 || next.FromCatalog)
        {
            Assign(next);
            return true;
        }

        if (string.Equals(_pending, next.Name, StringComparison.OrdinalIgnoreCase))
        {
            Assign(next);
            return true;
        }

        _pending = next.Name.Trim();
        return false;
    }

    private void Assign(MapChoice choice)
    {
        Value = choice.Name.Trim();
        MapId = choice.MapId;
        FromCatalog = choice.FromCatalog;
        _streetOnly = choice.StreetOnly;
        _pending = null;
        _doubt = false;
    }
}

public static class MapTitle
{
    public static MapChoice? Choose(IReadOnlyList<string>? lines)
    {
        if (lines == null || lines.Count == 0) return null;
        var cleaned = new List<string>();
        foreach (var line in lines)
        {
            var text = ScreenLabel.Normalize(line);
            if (text.Length == 0 || ScreenLabel.IsChrome(text)) continue;
            cleaned.Add(text);
        }

        if (cleaned.Count == 0) return null;

        (int Id, string Name)? best = null;
        var bestLine = -1;
        for (var i = 0; i < cleaned.Count; i++)
        {
            // "Zone 1" is a digit, not a roman I. Match the line as read,
            // then the repaired numeral, so Forest II still resolves.
            Consider(MapCatalog.Match(cleaned[i]), i);
            Consider(MapCatalog.Match(RepairNumeral(cleaned[i])), i);
        }

        var joined = string.Join(" ", cleaned);
        Consider(MapCatalog.Match(joined), -1);
        Consider(MapCatalog.Match(RepairNumeral(joined)), -1);
        if (best != null)
        {
            var streetOnly = bestLine >= 0 && cleaned.Skip(bestLine + 1).Any(line => ScreenLabel.ConfidentMap(line) != null);
            return new MapChoice(best.Value.Name, best.Value.Id, true, streetOnly);
        }

        string? fallback = null;
        foreach (var line in cleaned)
        {
            var place = ScreenLabel.ConfidentMap(line);
            if (place == null) continue;
            if (fallback == null || place.Length > fallback.Length) fallback = place;
        }

        return fallback == null ? null : new MapChoice(fallback, null, false);

        // On a tie the lower line wins: the map name sits under the street.
        void Consider((int Id, string Name)? hit, int line)
        {
            if (hit == null) return;
            var longer = best == null || hit.Value.Name.Length > best.Value.Name.Length;
            var lowerTie = best != null && bestLine >= 0 && line > bestLine && hit.Value.Name.Length == best.Value.Name.Length;
            if (!longer && !lowerTie) return;
            best = hit;
            bestLine = line;
        }
    }

    /// <summary>
    /// The roman numeral at the end of a map name is one pixel wide. OCR
    /// often reads I as l or 1. "Forest ll" is "Forest II". A smashed word
    /// such as "ForeWll" is left alone, because Forest I and Forest II are
    /// different maps.
    /// </summary>
    public static string RepairNumeral(string text)
    {
        return Regex.Replace(
            text,
            @"\s+([Il1|]{1,3})$",
            match => " " + new string('I', match.Groups[1].Value.Length));
    }

    /// <summary>
    /// The flat title panel reads cleanly as itself. The light-ink mask is
    /// for a name painted on the map, and that mask opens a gap in a thin
    /// letter ("Entrance" becomes "Entra nce"), which misses the catalog.
    /// Keep the panel read when it already names a map. Use the mask only
    /// when the panel read does not.
    /// </summary>
    public static IReadOnlyList<string> Prefer(IReadOnlyList<string>? panel, IReadOnlyList<string>? ink)
    {
        var panelLines = panel ?? [];
        var inkLines = ink ?? [];
        if (Choose(panelLines) is { FromCatalog: true, StreetOnly: false }) return panelLines;
        if (Choose(inkLines) is { FromCatalog: true, StreetOnly: false }) return inkLines;
        if (Choose(panelLines) is { FromCatalog: true }) return panelLines;
        if (Choose(inkLines) is { FromCatalog: true }) return inkLines;
        return panelLines.Count > 0 ? panelLines : inkLines;
    }
}

public static class ScreenLabel
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "of", "the", "to",
    };

    private static readonly HashSet<string> Jobs = new(StringComparer.OrdinalIgnoreCase)
    {
        "beginner", "warrior", "magician", "bowman", "thief", "pirate",
        "wizard", "cleric", "hunter", "assassin", "brawler", "gunslinger",
        "spearman", "fighter", "page", "archer", "bandit", "chief",
        "hermit", "priest", "ranger", "sniper", "crusader", "knight",
        "mage", "marauder", "outlaw", "crossbowman", "rogue", "swordman",
        "swordsman",
        "hp", "mp", "exp", "lv", "level", "members",
    };

    private static readonly HashSet<string> JobWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "beginner", "warrior", "magician", "bowman", "thief", "pirate",
        "wizard", "cleric", "hunter", "assassin", "brawler", "gunslinger",
        "spearman", "fighter", "page", "archer", "bandit", "chief",
        "hermit", "priest", "ranger", "sniper", "crusader", "knight",
        "mage", "marauder", "outlaw", "crossbowman", "rogue", "swordman",
        "swordsman", "white", "dragon",
    };

    /// <summary>
    /// The name plate read includes the job line ("Assassin" above "ExtraSin").
    /// A cursor over the plate usually hides part of it.
    /// </summary>
    public static bool HasJobLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        foreach (var piece in raw.Replace('\r', '\n').Split('\n'))
        {
            var first = Normalize(piece).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first != null && JobWords.Contains(first.Trim('(', ')'))) return true;
        }

        return false;
    }

    public static string Normalize(string? raw)
    {
        var text = Regex.Replace(raw ?? "", @"\s+", " ").Trim();
        return text.Trim('.', '!', ',', ':', ';', '"', '\'');
    }

    public static bool IsChrome(string? raw)
    {
        var key = Key(raw);
        return key is "mini map" or "minimap" or "map name" or "world" or "battle analysis" or "map";
    }

    public static string? ConfidentMap(string? raw)
    {
        var text = Normalize(raw);
        if (text.Length == 0 || IsChrome(text)) return null;
        if (text.Length > 48) text = text[..48].Trim();
        if (!MostlyWords(text)) return null;
        if (Regex.IsMatch(text, @"\b(exp|experience|mesos?|elapsed|gained|inventory)\b", RegexOptions.IgnoreCase))
        {
            return null;
        }

        var letters = Regex.Replace(text, "[^A-Za-z]", "");
        return letters.Length >= 3 ? text : null;
    }

    /// <summary>
    /// Picks a character name out of the status plate. The job line and
    /// stat labels are rejected. A star or other junk character is rejected,
    /// which drops the old loot misread of this name.
    /// </summary>
    public static string? AcceptCharacter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string? best = null;
        foreach (var piece in raw.Replace('\r', '\n').Split('\n'))
        {
            var line = Normalize(piece);
            if (line.Length == 0) continue;
            if (Regex.IsMatch(line, @"\b(exp|experience|mesos?|elapsed|gained|inventory|fame)\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            if (TryName(line, out var whole))
            {
                best = whole;
                continue;
            }

            // Names have no spaces. OCR splits "ExtraSin" into "Extra Sin".
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 2 or 3
                && parts.All(part => part.Any(char.IsLetter) && !Jobs.Contains(part))
                && TryName(string.Concat(parts), out var rejoined))
            {
                best = rejoined;
                continue;
            }

            if (parts.Length > 1 && TryName(parts[^1], out var last)) best = last;
        }

        return best;
    }

    /// <summary>
    /// True when two names differ by at most two edits, or one is a truncation
    /// of the other (OCR often cuts NehoraiSan down to Neho).
    /// </summary>
    public static bool NamesClose(string? left, string? right)
    {
        var a = (left ?? "").Trim();
        var b = (right ?? "").Trim();
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        var aLow = a.ToLowerInvariant();
        var bLow = b.ToLowerInvariant();
        // Prefix truncation: keep at least 4 shared letters so "Neho" matches
        // NehoraiSan without treating unrelated short names as the same.
        if (aLow.Length >= 4 && bLow.Length >= 4
            && (aLow.StartsWith(bLow, StringComparison.Ordinal) || bLow.StartsWith(aLow, StringComparison.Ordinal)))
        {
            return true;
        }

        if (Math.Abs(a.Length - b.Length) > 2) return false;
        if (a.Length < 4 || b.Length < 4) return false;
        return EditDistance(aLow, bLow) <= 2;
    }

    private static int EditDistance(string a, string b)
    {
        var rows = a.Length + 1;
        var cols = b.Length + 1;
        var grid = new int[rows, cols];
        for (var i = 0; i < rows; i++) grid[i, 0] = i;
        for (var j = 0; j < cols; j++) grid[0, j] = j;
        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < cols; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                grid[i, j] = Math.Min(
                    Math.Min(grid[i - 1, j] + 1, grid[i, j - 1] + 1),
                    grid[i - 1, j - 1] + cost);
            }
        }

        return grid[a.Length, b.Length];
    }

    public static bool SamePlace(string? left, string? right)
    {
        var a = Key(left);
        var b = Key(right);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Equals(b, StringComparison.Ordinal)) return true;
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return false;
        return wa.IsSubsetOf(wb) || wb.IsSubsetOf(wa);
    }

    private static bool TryName(string token, out string name)
    {
        name = "";
        if (token.Length is < 4 or > 12) return false;
        if (!token.All(char.IsLetterOrDigit)) return false;
        if (!token.Any(char.IsLetter)) return false;
        var letters = new string(token.Where(char.IsLetter).ToArray());
        if (letters.StartsWith("exp", StringComparison.OrdinalIgnoreCase)) return false;
        if (letters.StartsWith("meso", StringComparison.OrdinalIgnoreCase)) return false;
        if (Jobs.Contains(token)) return false;
        name = token;
        return true;
    }

    private static bool MostlyWords(string text)
    {
        var compact = Regex.Replace(text, @"\s", "");
        if (compact.Length == 0) return false;
        foreach (var ch in compact)
        {
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is '\'' or '-' or '<' or '>') continue;
            return false;
        }

        return true;
    }

    private static string Key(string? raw)
    {
        var text = Normalize(raw).ToLowerInvariant();
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static HashSet<string> Words(string key)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in key.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = Regex.Replace(word, "[^a-z0-9]", "");
            if (clean.Length == 0 || StopWords.Contains(clean)) continue;
            words.Add(clean);
        }

        return words;
    }
}
