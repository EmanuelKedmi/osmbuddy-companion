using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Farm;

public sealed class BattleSnapshot
{
    public int? Kills { get; init; }
    /// <summary>True when <see cref="Kills"/> was read from the Mob cell, not from another column.</summary>
    public bool KillsFromMob { get; init; }
    public long? DurationMs { get; init; }
    public long? Exp { get; init; }
    public long? Damage { get; init; }
    public long? Attacks { get; init; }
    public long? Meso { get; init; }
    public string Text { get; init; } = "";
    /// <summary>True when this snapshot came from labeled battle rows and was judged.</summary>
    public bool FrameChecked { get; init; }
    /// <summary>True when Dmg, Mob, Exp, and Meso agree, so the kill total may move.</summary>
    public bool FrameGood { get; init; }
    public string FrameReason { get; init; } = "";
}

/// <summary>
/// Reads the battle analysis window text. Kills and the time on the map are
/// the session counters. Damage here is what the window showed. It is not a
/// character-sheet formula.
/// </summary>
public static class BattleText
{
    private const int MaxText = 2000;

    private enum Stat { Dmg, Mob, Exp, Meso }

    private sealed class Piece
    {
        public Stat? Kind { get; init; }
        public long Value { get; init; }
        public bool Millions { get; init; }
        public char? Rate { get; init; }
        public bool Used { get; set; }
    }

    public static BattleSnapshot Parse(string text)
    {
        var source = text.Replace("\r", "");
        if (source.Length > MaxText) source = source[..MaxText];
        var readable = Normalize(Digits(source));
        var pieces = Tokenize(readable);
        var bound = Bind(pieces);
        long? kills = bound.TryGetValue(Stat.Mob, out var mob) ? mob : null;
        var fromMob = kills != null;
        var damage = Value(bound, Stat.Dmg);
        var exp = Value(bound, Stat.Exp);
        var meso = Value(bound, Stat.Meso);
        var gate = Gate(readable);
        if (gate.Checked && gate.Good)
        {
            kills = gate.Kills;
            fromMob = true;
            damage = gate.Damage;
            exp = gate.Exp;
            meso = gate.Meso;
        }
        else if (gate.Checked && !gate.Good && gate.MobIsDamage)
        {
            kills = null;
            fromMob = false;
        }

        return new BattleSnapshot
        {
            Kills = kills is long killCount and >= 0 and <= int.MaxValue ? (int)killCount : null,
            KillsFromMob = fromMob && kills is >= 0 and <= int.MaxValue,
            DurationMs = ParseDuration(readable) ?? Elapsed(readable),
            Exp = exp,
            Damage = damage,
            Attacks = LabeledLong(readable, @"\battacks?\b\s*[:=]\s*([\d,]+)"),
            Meso = meso,
            Text = source.Trim(),
            FrameChecked = gate.Checked,
            FrameGood = gate.Good,
            FrameReason = gate.Reason,
        };
    }

    public static bool ReadSucceeded(BattleSnapshot? battle)
    {
        return battle != null && (battle.Kills != null || battle.DurationMs != null);
    }

    /// <summary>
    /// Windows OCR reads a zero in this window as the letter O. Only tokens
    /// that are acting as digits are rewritten.
    /// </summary>
    private static string Digits(string text) => Regex.Replace(text, @"\b[Oo]\b", "0");

    /// <summary>
    /// The debug log joins OCR lines with a bar. The parser sees the same
    /// breaks the recognizer emitted.
    /// </summary>
    private static string Normalize(string text) => Regex.Replace(text, @"\s*\|\s*", "\n");

    private static readonly Regex Token = new(
        @"(\d{1,3}:\d{2}(?::\d{2})?)|(\d+\.\d+\s*M\b)|(\b(?:damage|dmg|mobs?|kills?|experience|exp|meso\w*)\b)|(\d[\d,]*)|(\([^)]*\))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ElapsedLine = new(
        @"elapsed|\bapsed\b|\btime\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<Piece> Tokenize(string text)
    {
        var pieces = new List<Piece>();
        foreach (var raw in text.Split('\n'))
        {
            if (ElapsedLine.IsMatch(raw)) continue;
            var matches = Token.Matches(raw);
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (match.Groups[1].Success) continue;
                if (match.Groups[5].Success) continue;
                var rate = RateAfter(raw, match, matches, ref i);
                if (match.Groups[2].Success)
                {
                    pieces.Add(new Piece { Value = Millions(match.Groups[2].Value), Millions = true, Rate = rate ?? 's' });
                    continue;
                }

                if (match.Groups[3].Success)
                {
                    var kind = AsStat(match.Groups[3].Value);
                    if (kind != null) pieces.Add(new Piece { Kind = kind });
                    continue;
                }

                var number = ParseLong(match.Groups[4].Value);
                if (number == null) continue;
                pieces.Add(new Piece { Value = number.Value, Rate = rate });
            }
        }

        return pieces;
    }

    private static char? RateAfter(string line, Match match, MatchCollection matches, ref int index)
    {
        if (index + 1 >= matches.Count) return null;
        var next = matches[index + 1];
        if (!next.Groups[5].Success) return null;
        if (line[(match.Index + match.Length)..next.Index].Trim().Length > 0) return null;
        index++;
        return RateOf(next.Value);
    }

    private static char? RateOf(string paren)
    {
        if (Regex.IsMatch(paren, @"/\s*s\b", RegexOptions.IgnoreCase)) return 's';
        if (Regex.IsMatch(paren, @"/\s*m\b", RegexOptions.IgnoreCase)) return 'm';
        if (Regex.IsMatch(paren, @"\d\s*m\b", RegexOptions.IgnoreCase)) return 'm';
        if (Regex.IsMatch(paren, @"\d\s*s\b", RegexOptions.IgnoreCase)) return 's';
        return null;
    }

    private static long Millions(string raw)
    {
        var match = Regex.Match(raw, @"(\d+)\.(\d+)");
        var whole = long.Parse(match.Groups[1].Value);
        var frac = match.Groups[2].Value;
        var fracScale = 1_000_000L;
        long fracValue = 0;
        foreach (var ch in frac)
        {
            fracScale /= 10;
            if (fracScale <= 0) break;
            fracValue += (ch - '0') * fracScale;
        }

        return whole * 1_000_000L + fracValue;
    }

    private static Stat? AsStat(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower.StartsWith("meso", StringComparison.Ordinal)) return Stat.Meso;
        return lower switch
        {
            "dmg" or "damage" or "omg" or "0mg" => Stat.Dmg,
            "mob" or "mobs" or "kill" or "kills" => Stat.Mob,
            "exp" or "experience" => Stat.Exp,
            _ => null,
        };
    }

    /// <summary>
    /// A label run followed by a value run is the aligned window. Anything
    /// else is bound by the neighbor on either side, so a value that OCR
    /// placed before its label stays with that label.
    /// </summary>
    private static Dictionary<Stat, long> Bind(List<Piece> pieces)
    {
        var result = new Dictionary<Stat, long>();
        if (pieces.Count == 0) return result;
        var firstNumber = -1;
        var lastLabel = -1;
        for (var i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Kind != null) lastLabel = i;
            else if (firstNumber < 0) firstNumber = i;
        }

        if (lastLabel >= 0 && firstNumber > lastLabel)
        {
            ZipHeader(result, pieces.Where(piece => piece.Kind != null).ToList(), pieces.Where(piece => piece.Kind == null).ToList());
            return result;
        }

        BindInterleaved(pieces, result);
        return result;
    }

    private static void ZipHeader(Dictionary<Stat, long> result, List<Piece> labels, List<Piece> numbers)
    {
        long? millions = null;
        var plain = new List<Piece>();
        foreach (var number in numbers)
        {
            if (!number.Millions)
            {
                plain.Add(number);
                continue;
            }

            millions ??= number.Value;
        }

        if (millions != null && !result.ContainsKey(Stat.Dmg)) result[Stat.Dmg] = millions.Value;
        var slots = millions == null ? labels : labels.Where(label => label.Kind != Stat.Dmg).ToList();
        if (plain.Count > slots.Count) return;
        for (var i = 0; i < plain.Count; i++)
        {
            var kind = slots[i].Kind!.Value;
            if (kind == Stat.Mob && !SmallMob(plain[i].Value)) continue;
            if (kind == Stat.Mob && plain.Count < slots.Count && plain[i].Value >= 10_000) continue;
            if (!result.ContainsKey(kind)) result[kind] = plain[i].Value;
        }
    }

    /// <summary>A mob cell is a small count. An M-suffix damage figure is not one.</summary>
    private static bool SmallMob(long value) => value is > 0 and < 100_000;

    private static void BindInterleaved(List<Piece> pieces, Dictionary<Stat, long> result)
    {
        foreach (var piece in pieces)
        {
            if (!piece.Millions) continue;
            if (!result.ContainsKey(Stat.Dmg)) result[Stat.Dmg] = piece.Value;
            piece.Used = true;
        }

        if (result.ContainsKey(Stat.Dmg))
        {
            foreach (var piece in pieces)
            {
                if (piece.Kind == Stat.Dmg) piece.Used = true;
            }
        }

        var segs = Segments(pieces.Where(piece => !piece.Used).ToList());
        for (var i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            if (IsLabels(seg) && i + 1 < segs.Count && IsNumbers(segs[i + 1]))
            {
                var labels = seg.Where(piece => piece.Kind != null && !piece.Used).ToList();
                var numbers = segs[i + 1].Where(piece => piece.Kind == null && !piece.Used).ToList();
                if (labels.Count == 0 || numbers.Count == 0) continue;
                if (numbers.Count == 1 && numbers[0].Rate == 'm' && labels[0].Kind == Stat.Dmg && MobLater(segs, i + 1) is Piece mob)
                {
                    Take(result, mob, numbers[0]);
                    continue;
                }

                // The numbers belong to the labels closest to them. A leading
                // Mob whose own figure was damage must not take Exp's number.
                var count = Math.Min(labels.Count, numbers.Count);
                var start = labels.Count - count;
                for (var n = 0; n < count; n++) Take(result, labels[start + n], numbers[n]);
            }
            else if (IsNumbers(seg) && i + 1 < segs.Count && IsLabels(segs[i + 1]))
            {
                var numbers = seg.Where(piece => piece.Kind == null && !piece.Used).ToList();
                var labels = segs[i + 1].Where(piece => piece.Kind != null && !piece.Used).ToList();
                if (numbers.Count == 0 || labels.Count == 0) continue;
                if (numbers.Count == 1)
                {
                    var number = numbers[0];
                    if (number.Rate == 's' || number.Millions)
                    {
                        if (!result.ContainsKey(Stat.Dmg)) result[Stat.Dmg] = number.Value;
                        number.Used = true;
                        var dmg = labels.FirstOrDefault(piece => piece.Kind == Stat.Dmg);
                        if (dmg != null) dmg.Used = true;
                        continue;
                    }

                    var label = number.Rate == 'm'
                        ? labels.FirstOrDefault(piece => piece.Kind == Stat.Mob) ?? labels[0]
                        : labels[0];
                    Take(result, label, number);
                    continue;
                }

                if (numbers.Count > labels.Count) continue;
                for (var n = 0; n < numbers.Count; n++) Take(result, labels[n], numbers[n]);
            }
        }
    }

    private static Piece? MobLater(List<List<Piece>> segs, int numberSegment)
    {
        if (numberSegment + 1 >= segs.Count || !IsLabels(segs[numberSegment + 1])) return null;
        return segs[numberSegment + 1].FirstOrDefault(piece => piece.Kind == Stat.Mob && !piece.Used);
    }

    private static void Take(Dictionary<Stat, long> result, Piece label, Piece number)
    {
        if (label.Kind == null || label.Used || number.Used) return;
        if (label.Kind == Stat.Mob && (number.Millions || !SmallMob(number.Value))) return;
        if (!result.ContainsKey(label.Kind.Value)) result[label.Kind.Value] = number.Value;
        label.Used = true;
        number.Used = true;
    }

    private static List<List<Piece>> Segments(List<Piece> pieces)
    {
        var segs = new List<List<Piece>>();
        foreach (var piece in pieces)
        {
            var label = piece.Kind != null;
            if (segs.Count == 0 || (segs[^1][0].Kind != null) != label) segs.Add([]);
            segs[^1].Add(piece);
        }

        return segs;
    }

    private static bool IsLabels(List<Piece> seg) => seg.Count > 0 && seg[0].Kind != null;

    private static bool IsNumbers(List<Piece> seg) => seg.Count > 0 && seg[0].Kind == null;

    private static long? Value(Dictionary<Stat, long> bound, Stat stat)
    {
        return bound.TryGetValue(stat, out var value) ? value : null;
    }

    private static long? Elapsed(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ElapsedLine.IsMatch(lines[i])) continue;
            var clock = Clock(lines[i]);
            if (clock != null) return clock;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (Regex.IsMatch(lines[j], @"\b(?:damage|dmg|mobs?|kills?|experience|exp|mesos?)\b", RegexOptions.IgnoreCase)) break;
                var next = Clock(lines[j]);
                if (next != null) return next;
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (Regex.IsMatch(line, @"^\d{1,3}:\d{2}$")) return Clock(line);
        }

        return null;
    }

    private static long? Clock(string line)
    {
        var clock = Regex.Match(line, @"(\d+):(\d{2})(?::(\d{2}))?");
        if (!clock.Success) return null;
        if (clock.Groups[3].Success)
        {
            var hours = int.Parse(clock.Groups[1].Value);
            var minutes = int.Parse(clock.Groups[2].Value);
            var seconds = int.Parse(clock.Groups[3].Value);
            if (minutes < 60 && seconds < 60) return ((hours * 60L + minutes) * 60 + seconds) * 1000;
            return null;
        }

        var minute = int.Parse(clock.Groups[1].Value);
        var second = int.Parse(clock.Groups[2].Value);
        if (second >= 60) return null;
        return (minute * 60L + second) * 1000;
    }

    private static long? LabeledLong(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return ParseLong(match.Groups[1].Value);
    }

    private static long? ParseDuration(string text)
    {
        var labeled = Regex.Match(text, @"time(?:\s+elapsed)?\s*[:=]\s*([^\n]+)", RegexOptions.IgnoreCase);
        if (!labeled.Success) return null;
        var source = labeled.Groups[1].Value.Trim();
        if (source.Length == 0) return null;

        var clock = Regex.Match(source, @"(\d+):(\d{1,2})(?::(\d{1,2}))?");
        if (clock.Success)
        {
            if (clock.Groups[3].Success)
            {
                var hours = int.Parse(clock.Groups[1].Value);
                var minutes = int.Parse(clock.Groups[2].Value);
                var seconds = int.Parse(clock.Groups[3].Value);
                if (minutes < 60 && seconds < 60) return ((hours * 60L + minutes) * 60 + seconds) * 1000;
            }
            else
            {
                var minutes = int.Parse(clock.Groups[1].Value);
                var seconds = int.Parse(clock.Groups[2].Value);
                if (seconds < 60) return (minutes * 60L + seconds) * 1000;
            }
        }

        var words = Regex.Match(
            source,
            @"^(?:(\d+)\s*h(?:ours?|rs?)?)?\s*(?:(\d+)\s*m(?:in(?:utes?)?)?)?\s*(?:(\d+)\s*s(?:ec(?:onds?)?)?)?$",
            RegexOptions.IgnoreCase);
        if (words.Success && (words.Groups[1].Success || words.Groups[2].Success || words.Groups[3].Success))
        {
            var hours = words.Groups[1].Success ? int.Parse(words.Groups[1].Value) : 0;
            var minutes = words.Groups[2].Success ? int.Parse(words.Groups[2].Value) : 0;
            var seconds = words.Groups[3].Success ? int.Parse(words.Groups[3].Value) : 0;
            return ((hours * 60L + minutes) * 60 + seconds) * 1000;
        }

        return null;
    }

    private static long? ParseLong(string raw)
    {
        if (!long.TryParse(raw.Replace(",", ""), out var parsed) || parsed < 0 || parsed > 10_000_000_000L) return null;
        return parsed;
    }

    private readonly record struct GatedRow(long Value, bool Millions, char? Rate);

    private readonly record struct GateResult(
        bool Checked,
        bool Good,
        string Reason,
        bool MobIsDamage,
        int? Kills,
        long? Damage,
        long? Exp,
        long? Meso);

    /// <summary>
    /// A frame is good only when each of Dmg, Mob, Exp, and Meso is named by
    /// its own label and the numbers agree: mob is a small count, damage is
    /// the large figure, and exp is above the mob count.
    /// </summary>
    private static GateResult Gate(string text)
    {
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var rows = new Dictionary<Stat, GatedRow>();
        var sawLabel = false;
        var usedValue = new bool[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var labels = Token.Matches(lines[i]).Where(match => match.Groups[3].Success).ToList();
            if (labels.Count != 1) continue;
            var stat = AsStat(labels[0].Groups[3].Value);
            if (stat == null) continue;
            sawLabel = true;
            if (TryRowValue(lines[i], out var onLine))
            {
                rows[stat.Value] = onLine;
                if (stat == Stat.Mob && (onLine.Millions || onLine.Rate == 's' || onLine.Value >= 10_000))
                {
                    return Bad("mob value is damage-scale", rows, mobIsDamage: true);
                }

                continue;
            }

            if (i > 0 && !usedValue[i - 1] && LabelCount(lines[i - 1]) == 0 && TryRowValue(lines[i - 1], out var before))
            {
                usedValue[i - 1] = true;
                rows[stat.Value] = before;
                continue;
            }

            if (i + 1 < lines.Length && LabelCount(lines[i + 1]) == 0 && TryRowValue(lines[i + 1], out var after))
            {
                usedValue[i + 1] = true;
                rows[stat.Value] = after;
            }
        }

        if (TryHeaderRows(text, out var header))
        {
            sawLabel = true;
            foreach (var pair in header) rows[pair.Key] = pair.Value;
        }

        if (!sawLabel && TryBareRows(text, out var bare))
        {
            foreach (var pair in bare) rows[pair.Key] = pair.Value;
            sawLabel = true;
        }

        if (!sawLabel) return new GateResult(false, false, "", false, null, null, null, null);

        // Whole-panel OCR on this client often skips the short "Dmg" word and
        // still returns the four values in row order after Mob, Exp, Meso.
        if (!rows.ContainsKey(Stat.Dmg) && TryDroppedDmg(text, out var dropped))
        {
            foreach (var pair in dropped) rows[pair.Key] = pair.Value;
        }

        if (!rows.ContainsKey(Stat.Dmg)) return Bad("missing Dmg label", rows);
        if (!rows.ContainsKey(Stat.Mob)) return Bad("missing Mob label", rows);
        if (!rows.ContainsKey(Stat.Exp)) return Bad("missing Exp label", rows);
        if (!rows.ContainsKey(Stat.Meso)) return Bad("missing Meso label", rows);

        var mob = rows[Stat.Mob];
        var dmg = rows[Stat.Dmg];
        var exp = rows[Stat.Exp];
        if (mob.Millions || mob.Rate == 's' || mob.Value >= 10_000)
        {
            return Bad("mob value is damage-scale", rows, mobIsDamage: true);
        }

        if (mob.Value <= 0) return Bad("missing Mob label", rows);
        var damageLarge = dmg.Millions || dmg.Rate == 's' || dmg.Value >= mob.Value * 5;
        if (!damageLarge) return Bad("rows disagree", rows, mobIsDamage: true);
        if (exp.Value <= mob.Value) return Bad("rows disagree", rows);

        return new GateResult(true, true, "", false, (int)mob.Value, dmg.Value, exp.Value, rows[Stat.Meso].Value);
    }

    private static GateResult Bad(string reason, Dictionary<Stat, GatedRow> rows, bool mobIsDamage = false)
    {
        rows.TryGetValue(Stat.Dmg, out var dmg);
        rows.TryGetValue(Stat.Exp, out var exp);
        rows.TryGetValue(Stat.Meso, out var meso);
        return new GateResult(
            true,
            false,
            reason,
            mobIsDamage,
            null,
            rows.ContainsKey(Stat.Dmg) ? dmg.Value : null,
            rows.ContainsKey(Stat.Exp) ? exp.Value : null,
            rows.ContainsKey(Stat.Meso) ? meso.Value : null);
    }

    private readonly record struct GateTok(Stat? Kind, GatedRow? Row);

    /// <summary>
    /// Windows OCR drops the short Dmg label and still emits Mob, Exp, Meso,
    /// then the four figures in row order. The first figure is the damage.
    /// </summary>
    private static bool TryDroppedDmg(string text, out Dictionary<Stat, GatedRow> rows)
    {
        rows = new Dictionary<Stat, GatedRow>();
        var labels = new List<Stat>();
        var values = new List<GatedRow>();
        var sawValue = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || ElapsedLine.IsMatch(line)) continue;
            if (line.Contains("battle", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(line, @"\d")) continue;
            var labelsOnLine = Token.Matches(line).Where(match => match.Groups[3].Success).ToList();
            if (!sawValue && labelsOnLine.Count == 1 && !TryRowValue(line, out _))
            {
                var stat = AsStat(labelsOnLine[0].Groups[3].Value);
                if (stat == null) continue;
                if (stat == Stat.Dmg) return false;
                labels.Add(stat.Value);
                continue;
            }

            if (!TryRowValue(line, out var row)) continue;
            sawValue = true;
            values.Add(row);
        }

        if (!labels.Contains(Stat.Mob) || !labels.Contains(Stat.Exp) || values.Count < 4) return false;
        if (!values[0].Millions && values[0].Value < 100_000) return false;
        if (values[1].Millions || values[1].Value >= 10_000) return false;
        rows[Stat.Dmg] = values[0];
        rows[Stat.Mob] = values[1];
        rows[Stat.Exp] = values[2];
        rows[Stat.Meso] = values[3];
        return true;
    }

    /// <summary>
    /// Whole-panel OCR prints every label, then the four values in that same
    /// order. A rate such as (181m) beside the mob count is only a rate.
    /// </summary>
    private static bool TryHeaderRows(string text, out Dictionary<Stat, GatedRow> header)
    {
        header = new Dictionary<Stat, GatedRow>();
        var toks = new List<GateTok>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Regex.Replace(raw.Trim(), @"\([^)\r\n]*\)?", " ");
            if (line.Length == 0 || ElapsedLine.IsMatch(line)) continue;
            var matches = Token.Matches(line);
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (match.Groups[1].Success || match.Groups[5].Success) continue;
                if (match.Groups[3].Success)
                {
                    var stat = AsStat(match.Groups[3].Value);
                    if (stat != null) toks.Add(new GateTok(stat, null));
                    continue;
                }

                GatedRow? row = null;
                if (match.Groups[2].Success)
                {
                    row = new GatedRow(Millions(match.Groups[2].Value), true, RateAfter(line, match, matches, ref i) ?? 's');
                }
                else if (match.Groups[4].Success)
                {
                    var value = ParseLong(match.Groups[4].Value);
                    if (value == null) continue;
                    row = new GatedRow(value.Value, false, RateAfter(line, match, matches, ref i));
                }

                if (row != null) toks.Add(new GateTok(null, row));
            }
        }

        var wanted = new[] { Stat.Dmg, Stat.Mob, Stat.Exp, Stat.Meso };
        for (var i = 0; i + 3 < toks.Count; i++)
        {
            var aligned = true;
            for (var n = 0; n < wanted.Length; n++)
            {
                if (toks[i + n].Kind != wanted[n]) aligned = false;
            }

            if (!aligned) continue;
            var values = new List<GatedRow>();
            for (var j = i + 4; j < toks.Count && values.Count < 4; j++)
            {
                if (toks[j].Kind != null) break;
                if (toks[j].Row is { } row) values.Add(row);
            }

            if (values.Count != 4) return false;
            for (var n = 0; n < 4; n++) header[wanted[n]] = values[n];
            return true;
        }

        return false;
    }

    /// <summary>
    /// The panel sometimes comes back as values only, in row order:
    /// damage, mob, exp, meso. A garbled rate on the mob count stays a rate.
    /// </summary>
    private static bool TryBareRows(string text, out Dictionary<Stat, GatedRow> rows)
    {
        rows = new Dictionary<Stat, GatedRow>();
        var values = new List<GatedRow>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || ElapsedLine.IsMatch(line)) continue;
            if (Token.Matches(line).Any(match => match.Groups[3].Success)) return false;
            var matches = Token.Matches(line);
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (match.Groups[1].Success || match.Groups[5].Success) continue;
                if (match.Groups[2].Success)
                {
                    values.Add(new GatedRow(Millions(match.Groups[2].Value), true, RateAfter(line, match, matches, ref i) ?? 's'));
                    continue;
                }

                if (!match.Groups[4].Success) continue;
                var value = ParseLong(match.Groups[4].Value);
                if (value == null) continue;
                values.Add(new GatedRow(value.Value, false, RateAfter(line, match, matches, ref i)));
            }
        }

        if (values.Count < 4) return false;
        var order = new[] { Stat.Dmg, Stat.Mob, Stat.Exp, Stat.Meso };
        for (var n = 0; n < 4; n++) rows[order[n]] = values[n];
        return true;
    }

    private static int LabelCount(string line) => Token.Matches(line).Count(match => match.Groups[3].Success);

    private static bool TryRowValue(string line, out GatedRow row)
    {
        row = default;
        var million = Regex.Match(line, @"\d+\.\d+\s*M\b", RegexOptions.IgnoreCase);
        if (million.Success)
        {
            var rate = RateOn(line);
            row = new GatedRow(Millions(million.Value), true, rate ?? 's');
            return true;
        }

        var bare = Regex.Replace(line, @"\([^)\r\n]*\)?", " ");
        var number = Regex.Match(bare, @"\d[\d,]*");
        if (!number.Success) return false;
        var value = ParseLong(number.Value);
        if (value == null) return false;
        row = new GatedRow(value.Value, false, RateOn(line));
        return true;
    }

    private static char? RateOn(string line)
    {
        var paren = Regex.Match(line, @"\([^)]*\)");
        return paren.Success ? RateOf(paren.Value) : null;
    }
}
