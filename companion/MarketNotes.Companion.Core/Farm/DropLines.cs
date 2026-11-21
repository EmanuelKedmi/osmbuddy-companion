using System.Text.RegularExpressions;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Farm;

public sealed class ParsedDrop
{
    public required string ItemName { get; init; }
    public required int Quantity { get; init; }
    public required string Raw { get; init; }
}

public sealed class NoticeFrame
{
    public string? RegionHash { get; set; }
    public List<string>? Lines { get; set; }
    public string? Error { get; set; }
    /// <summary>Top of each line in crop pixels, same order as Lines. With AtMs, lines are tracked by position.</summary>
    public List<int>? Tops { get; set; }
    public int? CropHeight { get; set; }
    public long? AtMs { get; set; }
}

/// <summary>A pickup line still on screen: its item, where it was last read, and when.</summary>
public sealed class NoticeTrack
{
    public required string Item { get; set; }
    public int Quantity { get; set; }
    public int Top { get; set; }
    public long SeenMs { get; set; }
    public long? EmptySinceMs { get; set; }
}

public sealed class NoticeCursor
{
    public string? RegionHash { get; set; }
    public List<string> Lines { get; set; } = [];
    /// <summary>Item|qty keys that actually parsed on the last read. Not recomputed later.</summary>
    public List<string> AcceptedKeys { get; set; } = [];
    public int? ParsedMapId { get; set; }
    public List<NoticeTrack> Tracks { get; set; } = [];
}

public sealed class IngestResult
{
    public required NoticeCursor Cursor { get; init; }
    public required List<ParsedDrop> Drops { get; init; }
    public int Ignored { get; init; }
    public int ParseErrors { get; init; }
    public string? LastError { get; init; }
    public int Skipped { get; init; }
    public int Read { get; init; }
}

/// <summary>
/// Pickup lines from the bottom-right loot corner. The same line still on
/// screen is not a new drop. An unchanged region hash is skipped entirely,
/// unless the map has since become known and a clipped name can now be confirmed.
/// Experience, fame, level-up, and other chat are ignored.
/// A drop name must be an exact catalog item, or the start of one. When several
/// catalog names share that start, only the one that drops on the current map is kept.
/// </summary>
public static class DropLines
{
    private const int MaxQty = 100_000_000;
    private const int MaxLines = 80;

    public static string NormalizeKey(string name)
    {
        return Regex.Replace(name ?? "", @"\s+", " ").Trim().ToLowerInvariant();
    }

    public static ParsedDrop? Parse(string input, int? mapId = null, string? mapName = null)
    {
        var line = Prepare(input);
        if (line.Length == 0 || IsNonDrop(line) || IsExperience(line)) return null;

        var mesoQty = MesoQuantity(line);
        if (mesoQty != null)
        {
            return new ParsedDrop { ItemName = "Mesos", Quantity = mesoQty.Value, Raw = input };
        }

        return Phrased(line, input, mapId, mapName) ?? TabPickup(line, input, mapId, mapName);
    }

    private static ParsedDrop? Phrased(string line, string input, int? mapId, string? mapName)
    {
        var leading = Regex.Match(line, @"^you have gained\s+([\d,]+)\s+(.+)$", RegexOptions.IgnoreCase);
        if (leading.Success)
        {
            var quantity = ParseQty(leading.Groups[1].Value);
            var itemName = Clean(leading.Groups[2].Value);
            if (quantity == null || !KnownItem(itemName, mapId, mapName, out itemName)) return null;
            return new ParsedDrop { ItemName = itemName, Quantity = quantity.Value, Raw = input };
        }

        var stacked = Regex.Match(line, @"^you have gained\s+(?:an?\s+)?(.+?)\s+x\s*([\d,]+)$", RegexOptions.IgnoreCase);
        if (stacked.Success)
        {
            var quantity = ParseQty(stacked.Groups[2].Value);
            var itemName = Clean(stacked.Groups[1].Value);
            if (quantity == null || !KnownItem(itemName, mapId, mapName, out itemName)) return null;
            return new ParsedDrop { ItemName = itemName, Quantity = quantity.Value, Raw = input };
        }

        var single = Regex.Match(line, @"^you have gained\s+(?:an?\s+)?(.+)$", RegexOptions.IgnoreCase);
        if (single.Success)
        {
            var itemName = Clean(single.Groups[1].Value);
            if (!KnownItem(itemName, mapId, mapName, out itemName)) return null;
            return new ParsedDrop { ItemName = itemName, Quantity = 1, Raw = input };
        }

        var other = Regex.Match(line, @"^(?:obtained|picked up)\s+(?:an?\s+)?(.+?)(?:\s+x\s*([\d,]+)|\s+\(([\d,]+)\))?$", RegexOptions.IgnoreCase);
        if (other.Success)
        {
            var qtyRaw = other.Groups[2].Success ? other.Groups[2].Value : other.Groups[3].Success ? other.Groups[3].Value : null;
            var quantity = qtyRaw == null ? 1 : ParseQty(qtyRaw);
            var itemName = Clean(other.Groups[1].Value);
            if (quantity == null || !KnownItem(itemName, mapId, mapName, out itemName)) return null;
            return new ParsedDrop { ItemName = itemName, Quantity = quantity.Value, Raw = input };
        }

        return null;
    }

    /// <summary>
    /// "…in the Etc tab (Cactus' Flower)" is a pickup however the start of the
    /// line reads ("You hsve gained", "have gaincd", or cut off entirely).
    /// The bracketed name must still be a catalog item.
    /// </summary>
    private static ParsedDrop? TabPickup(string line, string input, int? mapId, string? mapName)
    {
        if (!TabLine.IsMatch(line)) return null;
        var inner = Parenthetical(line);
        if (inner == null) return null;
        if (CatalogItem(inner, out var itemName)) return Single(itemName);
        var trimmed = DropTrailingScrap(inner);
        if (trimmed != null && CatalogItem(trimmed, out itemName)) return Single(itemName);
        var clipped = "";
        if (MatchPrefix(inner, mapId, mapName, ref clipped) == PrefixDecision.Accepted) return Single(clipped);
        var map = MapDrops.Resolve(mapId, mapName);
        var near = map == null ? null : MapDrops.NearOnMap(map.Value, trimmed ?? inner) ?? MapDrops.NearOnMap(map.Value, inner);
        return near == null ? null : Single(near);

        ParsedDrop Single(string name) => new() { ItemName = name, Quantity = 1, Raw = input };
    }

    private static readonly Regex TabLine = new(
        @"\b(?:etc|use|usc|eqp|equip|setup|set-up|cash)\s+ta\w{0,2}\s*[({]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Why a raw loot line was kept or dropped. Used by the OCR dump.
    /// </summary>
    public static string Explain(string input, int? mapId = null, string? mapName = null)
    {
        var parsed = Parse(input, mapId, mapName);
        if (parsed != null)
        {
            return parsed.ItemName == "Mesos"
                ? $"kept meso {parsed.Quantity}"
                : $"kept {parsed.ItemName} x{parsed.Quantity}";
        }

        var line = Prepare(input);
        if (line.Length == 0) return "dropped blank";
        if (IsNonDrop(line)) return "dropped non-drop";
        if (IsExperience(line)) return "dropped experience";
        if (!Regex.IsMatch(line, @"you have gained|obtained|picked up", RegexOptions.IgnoreCase) && !TabLine.IsMatch(line))
            return "dropped, no gain phrase";
        return "dropped, name did not match a catalog item";
    }

    public static List<string> FreshLines(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var remaining = previous.Select(NormalizeKey).ToList();
        var fresh = new List<string>();
        foreach (var line in current)
        {
            var key = NormalizeKey(line);
            var index = remaining.IndexOf(key);
            if (index >= 0) remaining.RemoveAt(index);
            else if (line.Trim().Length > 0) fresh.Add(line);
        }

        return fresh;
    }

    public static IngestResult Ingest(NoticeCursor cursor, IReadOnlyList<NoticeFrame> frames, int? mapId = null, string? mapName = null)
    {
        var resolved = MapDrops.Resolve(mapId, mapName);
        var regionHash = cursor.RegionHash;
        var lines = cursor.Lines.ToList();
        var accepted = cursor.AcceptedKeys?.ToList() ?? [];
        var parsedMap = cursor.ParsedMapId;
        var tracks = (cursor.Tracks ?? []).Select(track => new NoticeTrack
        {
            Item = track.Item,
            Quantity = track.Quantity,
            Top = track.Top,
            SeenMs = track.SeenMs,
            EmptySinceMs = track.EmptySinceMs,
        }).ToList();
        var drops = new List<ParsedDrop>();
        var ignored = 0;
        var parseErrors = 0;
        string? lastError = null;
        var skipped = 0;
        var read = 0;

        foreach (var frame in frames)
        {
            var hash = string.IsNullOrWhiteSpace(frame.RegionHash) ? null : frame.RegionHash.Trim();
            if (!string.IsNullOrWhiteSpace(frame.Error))
            {
                parseErrors++;
                lastError = frame.Error.Trim();
                if (lastError.Length > 240) lastError = lastError[..240];
                if (hash != null) regionHash = hash;
                continue;
            }

            if (frame.Lines == null)
            {
                parseErrors++;
                lastError = "This frame had no drop-notice lines to read.";
                if (hash != null) regionHash = hash;
                continue;
            }

            if (hash != null && regionHash != null && hash == regionHash && parsedMap == resolved)
            {
                skipped++;
                continue;
            }

            read++;
            regionHash = hash;
            if (frame.Tops != null && frame.AtMs != null && frame.Tops.Count == frame.Lines.Count)
            {
                var previous = lines.Select(NormalizeKey).ToList();
                lines = frame.Lines.Take(MaxLines).ToList();
                accepted = Track(tracks, frame, previous, mapId, mapName, drops, ref ignored);
                parsedMap = resolved;
                continue;
            }

            var previousDrops = accepted.ToList();
            var previousText = lines.Select(NormalizeKey).ToList();
            lines = frame.Lines.Take(MaxLines).ToList();
            var nextAccepted = new List<string>();
            foreach (var line in frame.Lines)
            {
                var parsed = Parse(line, mapId, mapName);
                if (parsed == null)
                {
                    if (line.Trim().Length > 0 && !previousText.Remove(NormalizeKey(line))) ignored++;
                    continue;
                }

                var key = DropKey(parsed);
                nextAccepted.Add(key);
                var index = previousDrops.IndexOf(key);
                if (index >= 0)
                {
                    previousDrops.RemoveAt(index);
                    continue;
                }

                drops.Add(parsed);
            }

            accepted = nextAccepted;
            parsedMap = resolved;
        }

        return new IngestResult
        {
            Cursor = new NoticeCursor
            {
                RegionHash = regionHash,
                Lines = lines,
                AcceptedKeys = accepted,
                ParsedMapId = parsedMap,
                Tracks = tracks,
            },
            Drops = drops,
            Ignored = ignored,
            ParseErrors = parseErrors,
            LastError = lastError,
            Skipped = skipped,
            Read = read,
        };
    }

    /// <summary>A track not read again within this long is gone, even if its slot never looked empty.</summary>
    public const long TrackTtlMs = 3000;

    /// <summary>
    /// A slot must look empty this long before its track ends. Single reads
    /// mid-transition see only a line or two, and ending every track on one of
    /// those counted the whole stack again on the next read.
    /// </summary>
    public const long TrackEmptyMs = 400;

    /// <summary>
    /// Many reads a second see the same line many times, sometimes garbled in
    /// between. Notices only move up, and a new one enters at the bottom, so a
    /// pickup is the same line as a track of the same item that sat at or
    /// below it (amounts can misread between reads). Anything else is a new
    /// drop. A track ends when its slot and the line above it stay empty for
    /// <see cref="TrackEmptyMs"/>, or after <see cref="TrackTtlMs"/>.
    /// </summary>
    private static List<string> Track(
        List<NoticeTrack> tracks,
        NoticeFrame frame,
        List<string> previousText,
        int? mapId,
        string? mapName,
        List<ParsedDrop> drops,
        ref int ignored)
    {
        var at = frame.AtMs!.Value;
        var height = frame.CropHeight is > 0 ? frame.CropHeight.Value : 200;
        var tolerance = Math.Max(4, height * 3 / 100);
        var lineHeight = Math.Max(8, height * 9 / 100);
        var tops = frame.Tops!;
        foreach (var track in tracks)
        {
            var occupied = tops.Any(top => top >= track.Top - lineHeight * 3 / 2 && top <= track.Top + tolerance);
            if (occupied) track.EmptySinceMs = null;
            else track.EmptySinceMs ??= at;
        }

        tracks.RemoveAll(track => at - track.SeenMs > TrackTtlMs
            || (track.EmptySinceMs is long empty && at - empty >= TrackEmptyMs));

        var pickups = new List<(ParsedDrop Drop, int Top)>();
        for (var i = 0; i < frame.Lines!.Count; i++)
        {
            var line = frame.Lines[i];
            var parsed = Parse(line, mapId, mapName);
            if (parsed == null)
            {
                if (line.Trim().Length > 0 && !previousText.Remove(NormalizeKey(line))) ignored++;
                continue;
            }

            pickups.Add((parsed, tops[i]));
        }

        var used = new HashSet<NoticeTrack>();
        var accepted = new List<string>();
        foreach (var (drop, top) in pickups.OrderBy(pickup => pickup.Top))
        {
            accepted.Add(DropKey(drop));
            var item = NormalizeKey(drop.ItemName);
            var same = tracks
                .Where(track => !used.Contains(track) && track.Item == item && track.Top >= top - tolerance)
                .OrderBy(track => track.Quantity == drop.Quantity ? 0 : 1)
                .ThenBy(track => track.Top - top)
                .FirstOrDefault();
            if (same != null)
            {
                used.Add(same);
                same.Top = top;
                same.SeenMs = at;
                same.Quantity = drop.Quantity;
                continue;
            }

            var fresh = new NoticeTrack { Item = item, Quantity = drop.Quantity, Top = top, SeenMs = at };
            tracks.Add(fresh);
            used.Add(fresh);
            drops.Add(drop);
        }

        return accepted;
    }

    private static string Prepare(string input)
    {
        var line = Regex.Replace(input ?? "", @"\s+", " ").Trim();
        line = Regex.Replace(line, @"^\[[^\]]{0,40}\]\s*", "");
        line = Regex.Replace(line, @"#c", "", RegexOptions.IgnoreCase).Replace("#", "");
        line = Regex.Replace(line, @"[.!]+$", "").Trim();
        return line;
    }

    private static int? MesoQuantity(string line)
    {
        // OCR reads the m in "mesos" as "nn", "rn", "[n", or "Ili".
        const string meso = @"(?:m|nn|rn|\[n|ili)esos?";
        var gained = Regex.Match(line, @"you have gained\s+([\d,]+)\s+" + meso + @"\b", RegexOptions.IgnoreCase);
        if (gained.Success) return ParseQty(gained.Groups[1].Value);
        var wrapped = Regex.Match(line, @"(?<![a-z])" + meso + @"\s*\(\s*(?<sign>[^\d)\s]{0,3})\s*(?<n>[\d,]+)(?<tail>.*)$", RegexOptions.IgnoreCase);
        if (wrapped.Success) return WrappedQty(wrapped.Groups["sign"].Value, wrapped.Groups["n"].Value, wrapped.Groups["tail"].Value);
        var plus = Regex.Match(line, @"(?<![a-z])" + meso + @"\s+\+\s*([\d,]+)\b", RegexOptions.IgnoreCase);
        if (plus.Success) return ParseQty(plus.Groups[1].Value);
        return null;
    }

    /// <summary>
    /// The amount in "mesos (+181)". A letter or mark right after the digits
    /// ("+19!", "+22c") means a digit was misread, so the amount is not trusted.
    /// With no sign before it, a leading 1 or 4 on four digits is the "+"
    /// misread ("(4153)" is +153).
    /// </summary>
    private static int? WrappedQty(string sign, string digits, string tail)
    {
        var rest = tail.Trim();
        if (rest.Length > 0 && rest[0] is not (')' or '}' or ']' or ':' or ';')) return null;
        if (sign.Length == 0 && !digits.Contains(',') && digits.Length >= 4 && digits[0] is '1' or '4') digits = digits[1..];
        return ParseQty(digits);
    }

    /// <summary>
    /// Experience notices are not loot. OCR often splits the word, so a line
    /// whose letters are a broken "experience" is the same notice.
    /// </summary>
    private static bool IsExperience(string line)
    {
        var body = Regex.Replace(line, @"^you have gained\s+(?:an?\s+)?", "", RegexOptions.IgnoreCase);
        var letters = Regex.Replace(body, "[^a-z]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        if (letters.Length == 0) return false;
        if (letters is "exp" or "fame") return true;
        if (letters.StartsWith("exp", StringComparison.Ordinal) && letters.Length <= 12) return true;
        if (letters.StartsWith("exer", StringComparison.Ordinal) && letters.Length <= 12) return true;
        return false;
    }

    private enum PrefixDecision { None, Accepted, Rejected }

    private static bool KnownItem(string name, int? mapId, string? mapName, out string canonical)
    {
        canonical = "";
        var inner = Parenthetical(name);
        if (inner != null && CatalogItem(inner, out canonical)) return true;
        var trimmed = inner == null ? null : DropTrailingScrap(inner);
        if (trimmed != null && CatalogItem(trimmed, out canonical)) return true;

        var tab = Regex.Match(name, @"\(([^)]+)\)\s*$");
        if (tab.Success && Regex.IsMatch(name, @"\btab\b", RegexOptions.IgnoreCase))
        {
            name = tab.Groups[1].Value.Trim();
        }

        var shown = PriceCatalog.CanonicalItemName(name);
        if (PriceCatalog.TryExact(shown, out canonical)) return true;
        if (!shown.Equals(name, StringComparison.Ordinal) && PriceCatalog.TryExact(name, out canonical)) return true;

        var decision = MatchPrefix(shown, mapId, mapName, ref canonical);
        if (decision == PrefixDecision.Accepted) return true;
        if (decision == PrefixDecision.Rejected) return false;
        if (!shown.Equals(name, StringComparison.Ordinal))
        {
            decision = MatchPrefix(name, mapId, mapName, ref canonical);
            if (decision == PrefixDecision.Accepted) return true;
            if (decision == PrefixDecision.Rejected) return false;
        }

        if (PriceCatalog.TryMatch(shown, out canonical)) return true;
        return !shown.Equals(name, StringComparison.Ordinal) && PriceCatalog.TryMatch(name, out canonical);
    }

    /// <summary>
    /// Pickup text is "an item in the Etc tab (Cursed Doll". The closing
    /// parenthesis is often cut off, so the name is whatever follows the last
    /// opening parenthesis.
    /// </summary>
    private static string? Parenthetical(string name)
    {
        // "(Table Cloc(" ends on a stray bracket read from the cut-off one.
        name = name.TrimEnd().TrimEnd('(', '{').TrimEnd();
        var open = name.LastIndexOf('(');
        if (open < 0) open = name.LastIndexOf('{');
        if (open < 0 || open >= name.Length - 1) return null;
        var inner = name[(open + 1)..].Trim().TrimEnd(')', '}', '.', '!', ',').Trim();
        return inner.Length < 3 ? null : inner;
    }

    /// <summary>"Tablc Clock J'": a last word of one or two characters after the name is a scrap of the closing bracket.</summary>
    private static string? DropTrailingScrap(string inner)
    {
        var match = Regex.Match(inner, @"^(.{3,}?)\s+\S{1,2}$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool CatalogItem(string name, out string canonical)
    {
        canonical = "";
        var shown = PriceCatalog.CanonicalItemName(name);
        if (PriceCatalog.TryExact(shown, out canonical)) return true;
        if (!shown.Equals(name, StringComparison.Ordinal) && PriceCatalog.TryExact(name, out canonical)) return true;
        if (PriceCatalog.TryMatch(shown, out canonical)) return true;
        return !shown.Equals(name, StringComparison.Ordinal) && PriceCatalog.TryMatch(name, out canonical);
    }

    /// <summary>
    /// A clipped name is the start of a catalog item. One such item is enough.
    /// Several wait for the map, and only a drop from that map is kept.
    /// </summary>
    private static PrefixDecision MatchPrefix(string name, int? mapId, string? mapName, ref string canonical)
    {
        var hits = MapDrops.NamesStartingWith(name);
        if (hits.Count == 0) return PrefixDecision.None;
        if (hits.Count == 1)
        {
            canonical = hits[0];
            return PrefixDecision.Accepted;
        }

        var map = MapDrops.Resolve(mapId, mapName);
        if (map == null) return PrefixDecision.Rejected;

        var onMap = new List<(string Name, int Chance)>();
        foreach (var hit in hits)
        {
            if (MapDrops.TryChance(map.Value, hit, out var chance)) onMap.Add((hit, chance));
        }

        if (onMap.Count == 0) return PrefixDecision.Rejected;
        if (onMap.Count == 1)
        {
            canonical = onMap[0].Name;
            return PrefixDecision.Accepted;
        }

        var key = NormalizeKey(name);
        var starters = onMap.Where(row => NormalizeKey(row.Name).StartsWith(key, StringComparison.Ordinal)).ToList();
        var pool = starters.Count > 0 ? starters : onMap;
        var longest = pool.Max(row => row.Name.Length);
        var byLength = pool.Where(row => row.Name.Length == longest).ToList();
        var bestChance = byLength.Max(row => row.Chance);
        var winners = byLength.Where(row => row.Chance == bestChance).ToList();
        if (winners.Count != 1) return PrefixDecision.Rejected;
        canonical = winners[0].Name;
        return PrefixDecision.Accepted;
    }

    private static string DropKey(ParsedDrop drop) =>
        drop.ItemName.Trim().ToLowerInvariant() + "|" + drop.Quantity;

    private static bool IsNonDrop(string line)
    {
        if (Regex.IsMatch(line, @"level(?:ed|ing)? up\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(line, @"^quest complete\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(line, @"^you have gained\s+(?:\d[\d,]*\s+)?(?:exp|experience|fame)\b", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static string Clean(string name) => Regex.Replace(name, @"\s+", " ").Trim();

    private static int? ParseQty(string raw)
    {
        if (!int.TryParse(raw.Replace(",", ""), out var parsed) || parsed < 1 || parsed > MaxQty) return null;
        return parsed;
    }
}
