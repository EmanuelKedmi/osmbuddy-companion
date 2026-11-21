using System.Globalization;
using System.Text.RegularExpressions;
using MarketNotes.Companion.Farm;

namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// Replays loot-ocr.log through the drop counter in the order the companion
/// read it, and lists every line the parser kept that was not counted.
///   dotnet run --project companion/MarketNotes.Companion.InkHarness -c Release -- --replay-loot "%LOCALAPPDATA%\MarketNotes.Companion\loot-ocr.log" --from "2026-11-08 21:00" --to "2026-11-08 22:00"
/// Frames with no text are skipped, as the share does.
/// </summary>
static class LootReplay
{
    private sealed record Block(DateTime At, string Hash, string Map, int? MapId, List<string> Lines, List<int>? Tops, int CropHeight);

    public static int Run(string path, string? from, string? to)
    {
        var start = from == null ? DateTime.MinValue : DateTime.Parse(from, CultureInfo.InvariantCulture);
        var end = to == null ? DateTime.MaxValue : DateTime.Parse(to, CultureInfo.InvariantCulture);
        var blocks = Read(path).Where(b => b.At >= start && b.At <= end).ToList();

        var cursor = new NoticeCursor();
        string? map = null;
        var kept = 0;
        var counted = 0;
        var lost = 0;
        var tally = new Dictionary<string, (int Kept, int Counted)>();
        var perMap = new SortedDictionary<string, long>(StringComparer.Ordinal);
        List<string>? previous = null;
        foreach (var block in blocks)
        {
            var tracked = block.Tops != null && block.Tops.Count == block.Lines.Count;
            if (block.Lines.Count == 0 && !tracked) continue;
            if (map != block.Map)
            {
                map = block.Map;
                cursor = new NoticeCursor();
                previous = null;
                Console.WriteLine($"== {block.At:HH:mm:ss} map {block.Map}");
            }

            var parsed = block.Lines.Select(line => DropLines.Parse(line, block.MapId, block.Map)).Where(d => d != null).ToList();
            var frame = new NoticeFrame { RegionHash = block.Hash, Lines = block.Lines };
            if (tracked)
            {
                frame.Tops = block.Tops;
                frame.CropHeight = block.CropHeight;
                frame.AtMs = new DateTimeOffset(block.At).ToUnixTimeMilliseconds();
            }

            var result = DropLines.Ingest(cursor, [frame], block.MapId, block.Map);
            cursor = result.Cursor;
            kept += parsed.Count;
            counted += result.Drops.Count;
            foreach (var drop in parsed) Bump(drop!.ItemName, k: 1);
            foreach (var drop in result.Drops)
            {
                Bump(drop.ItemName, c: 1);
                var key = $"{block.Map} | {drop.ItemName}";
                perMap[key] = perMap.GetValueOrDefault(key) + drop.Quantity;
                if (tracked) Console.WriteLine($"  + {block.At:HH:mm:ss.fff} {drop.ItemName} x{drop.Quantity}   tops {string.Join(',', block.Tops!)}");
            }

            if (!tracked && parsed.Count > result.Drops.Count)
            {
                lost += parsed.Count - result.Drops.Count;
                Console.WriteLine($"{block.At:HH:mm:ss} kept {parsed.Count}, counted {result.Drops.Count}");
                Console.WriteLine("   before: " + string.Join(" | ", previous ?? []));
                Console.WriteLine("   now:    " + string.Join(" | ", block.Lines));
            }

            previous = block.Lines;
        }

        Console.WriteLine();
        Console.WriteLine($"frames {blocks.Count}, kept lines {kept}, counted {counted}, kept but not counted {lost}");
        foreach (var pair in tally.OrderByDescending(p => p.Value.Kept))
        {
            Console.WriteLine($"  {pair.Key}: kept on {pair.Value.Kept} frames, counted {pair.Value.Counted}");
        }

        Console.WriteLine();
        Console.WriteLine("counted per map:");
        foreach (var pair in perMap) Console.WriteLine($"  {pair.Key} x{pair.Value}");
        return 0;

        void Bump(string item, int k = 0, int c = 0)
        {
            tally.TryGetValue(item, out var row);
            tally[item] = (row.Kept + k, row.Counted + c);
        }
    }

    private static IEnumerable<Block> Read(string path)
    {
        Block? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var header = Regex.Match(raw, @"^--- (\S+ \S+) .*? crop \d+x(\d+) hash (\S+)(?: tops (\S+))? map (.*) \((\d+|\?)\)\s*$");
            if (header.Success)
            {
                if (current != null) yield return current;
                var at = DateTime.ParseExact(header.Groups[1].Value, ["yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None);
                int? id = int.TryParse(header.Groups[6].Value, out var parsedId) ? parsedId : null;
                List<int>? tops = !header.Groups[4].Success ? null
                    : header.Groups[4].Value == "-" ? []
                    : header.Groups[4].Value.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToList();
                current = new Block(at, header.Groups[3].Value, header.Groups[5].Value.Trim(), id, [], tops, int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture));
                continue;
            }

            if (current != null && raw.StartsWith("RAW ", StringComparison.Ordinal)) current.Lines.Add(raw[4..]);
            if (current != null && current.Lines.Count > 0 && raw.StartsWith("    dropped", StringComparison.Ordinal))
            {
                var line = current.Lines[^1];
                var now = DropLines.Parse(line, current.MapId, current.Map);
                if (now != null) Console.WriteLine($"   newly kept: {line}  =>  {now.ItemName} x{now.Quantity}");
            }
        }

        if (current != null) yield return current;
    }
}
