using System.Diagnostics;
using System.Globalization;
using System.Text;
using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// Battle analysis crops through several OCR engines, scored on the figures the companion uses.
///   --battle-bench collect   copies samples\battle into ocr-bench\battle\crops
///   --battle-bench run       parses each engine's read with BattleText and scores it against truth
/// Truth is crops\NAME.truth.txt: "dmg=59955 mob=18 exp=2862 meso=846 elapsed=04:18".
/// A crop where every engine that passed the gate agrees, and at least two did, gets NAME.auto.txt;
/// rename it to .truth.txt after a look at the image.
/// </summary>
static class BattleBench
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarketNotes.Companion", "ocr-bench", "battle");

    /// <summary>--crops DIR runs another folder, such as a handful of failures.</summary>
    public static string Crops { get; set; } = Path.Combine(Root, "crops");

    private sealed record Crop(string Name, byte[] Bgra, int Width, int Height, Figures? Truth);

    private sealed record Figures(long? Dmg, long? Mob, long? Exp, long? Meso, long? Elapsed)
    {
        public override string ToString() =>
            $"dmg={Show(Dmg)} mob={Show(Mob)} exp={Show(Exp)} meso={Show(Meso)} elapsed={(Elapsed is long ms ? $"{ms / 60000:00}:{ms / 1000 % 60:00}" : "-")}";

        private static string Show(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
    }

    private sealed record Engine(string Name, string About, Func<Crop, List<string>> Read);

    private sealed class Tally
    {
        public int Frames, Good, FalseGood, Right, Wrong, Missing, Fields;
        public long Ms, CpuMs;
    }

    public static int Run(string step, string? only) => step switch
    {
        "collect" => Collect(),
        "run" => Score(only),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.WriteLine("--battle-bench collect | run [--engines a,b]");
        return 1;
    }

    private static int Collect()
    {
        Directory.CreateDirectory(Crops);
        var source = Path.Combine(Root, "..", "..", "samples", "battle");
        var added = 0;
        foreach (var path in Directory.Exists(source) ? Directory.EnumerateFiles(source, "*.png") : [])
        {
            var target = Path.Combine(Crops, "sample-" + Path.GetFileName(path));
            if (File.Exists(target)) continue;
            File.Copy(path, target);
            added++;
        }

        Console.WriteLine($"added {added}, {Directory.EnumerateFiles(Crops, "*.png").Count()} crops in {Crops}");
        return 0;
    }

    private static int Score(string? only)
    {
        var crops = Directory.EnumerateFiles(Crops, "*.png").OrderBy(path => path, StringComparer.Ordinal).Select(Load).ToList();
        if (crops.Count == 0) return Usage();
        var engines = Engines();
        if (!string.IsNullOrWhiteSpace(only))
        {
            var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            engines = engines.Where(engine => wanted.Contains(engine.Name)).ToList();
        }

        Console.WriteLine($"{crops.Count} crops, {crops.Count(crop => crop.Truth != null)} with truth, {engines.Count} engines");
        var reads = new Dictionary<(string Engine, string Crop), (List<string> Lines, BattleSnapshot Parsed)>();
        var tallies = new Dictionary<string, Tally>();
        foreach (var engine in engines)
        {
            var tally = tallies[engine.Name] = new Tally();
            engine.Read(crops[0]);
            foreach (var crop in crops)
            {
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                var lines = engine.Read(crop);
                clock.Stop();
                tally.Ms += clock.ElapsedMilliseconds;
                tally.CpuMs += (long)(Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
                var parsed = BattleText.Parse(string.Join("\n", lines));
                reads[(engine.Name, crop.Name)] = (lines, parsed);
                Add(tally, crop, parsed);
            }

            Console.WriteLine($"{engine.Name,-14} {Summary(tally)}");
        }

        var auto = 0;
        foreach (var crop in crops.Where(crop => crop.Truth == null))
        {
            var good = engines.Select(engine => reads[(engine.Name, crop.Name)].Parsed).Where(parsed => parsed.FrameGood).Select(Of).ToList();
            if (good.Count < 2 || good.Distinct().Count() != 1 || good[0].Elapsed == null) continue;
            File.WriteAllText(Path.Combine(Crops, crop.Name + ".auto.txt"), good[0] + "\n");
            auto++;
        }

        var report = new StringBuilder();
        report.AppendLine($"Battle bench {DateTime.Now:yyyy-MM-dd HH:mm}  crops {crops.Count}, with truth {crops.Count(crop => crop.Truth != null)}, new auto labels {auto}");
        report.AppendLine("good = frames that passed the gate with all five figures right. falseGood = passed the gate with a figure wrong (kills can move on those).");
        report.AppendLine("right / wrong / missing = figures over all truth crops.");
        foreach (var engine in engines) report.AppendLine($"{engine.Name,-14} {Summary(tallies[engine.Name])}   {engine.About}");
        report.AppendLine();
        foreach (var crop in crops)
        {
            report.AppendLine($"{crop.Name}  truth {crop.Truth?.ToString() ?? "(none)"}");
            foreach (var engine in engines)
            {
                var (lines, parsed) = reads[(engine.Name, crop.Name)];
                var mark = crop.Truth == null ? " " : parsed.FrameGood && Of(parsed) == crop.Truth ? "+" : parsed.FrameGood ? "!" : "-";
                report.AppendLine($"  {mark} {engine.Name,-14} {Of(parsed)}  gate {(parsed.FrameGood ? "good" : parsed.FrameReason)}  | {string.Join(" | ", lines)}");
            }
        }

        File.WriteAllText(Path.Combine(Crops, "..", Path.GetFileName(Crops) + "-report.txt"), report.ToString());
        Console.WriteLine($"new auto labels {auto}. {Path.GetFullPath(Path.Combine(Crops, "..", Path.GetFileName(Crops) + "-report.txt"))}");
        return 0;
    }

    private static Figures Of(BattleSnapshot parsed) => new(parsed.Damage, parsed.Kills, parsed.Exp, parsed.Meso, parsed.DurationMs);

    private static void Add(Tally tally, Crop crop, BattleSnapshot parsed)
    {
        tally.Frames++;
        if (crop.Truth is not { } truth) return;
        var got = Of(parsed);
        var pairs = new[] { (truth.Dmg, got.Dmg), (truth.Mob, got.Mob), (truth.Exp, got.Exp), (truth.Meso, got.Meso), (truth.Elapsed, got.Elapsed) };
        foreach (var (want, have) in pairs)
        {
            tally.Fields++;
            if (have == null) tally.Missing++;
            else if (have == want) tally.Right++;
            else tally.Wrong++;
        }

        if (parsed.FrameGood && got == truth) tally.Good++;
        else if (parsed.FrameGood) tally.FalseGood++;
    }

    private static string Summary(Tally tally)
    {
        var truthFrames = tally.Fields / 5;
        var ms = tally.Frames == 0 ? 0 : tally.Ms / tally.Frames;
        var cpu = tally.Frames == 0 ? 0 : tally.CpuMs / tally.Frames;
        return string.Create(CultureInfo.InvariantCulture,
            $"good {tally.Good,3}/{truthFrames,-3} falseGood {tally.FalseGood,3}  right {tally.Right,4} wrong {tally.Wrong,3} missing {tally.Missing,3} of {tally.Fields,-4} {ms,4} ms  cpu {cpu,4} ms");
    }

    private static Crop Load(string path)
    {
        var image = Pixels.Load(path);
        var truthPath = Path.ChangeExtension(path, ".truth.txt");
        var truth = File.Exists(truthPath) ? ParseTruth(File.ReadAllText(truthPath)) : null;
        return new Crop(Path.GetFileNameWithoutExtension(path), image.Bgra, image.Width, image.Height, truth);
    }

    private static Figures? ParseTruth(string text)
    {
        var values = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
        long? Number(string key) => values.TryGetValue(key, out var raw) && long.TryParse(raw.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        long? elapsed = null;
        if (values.TryGetValue("elapsed", out var clock) && clock.Split(':') is { Length: >= 2 } parts)
        {
            elapsed = parts.Aggregate(0L, (sum, part) => sum * 60 + long.Parse(part, CultureInfo.InvariantCulture)) * 1000;
        }

        return new Figures(Number("dmg"), Number("mob"), Number("exp"), Number("meso"), elapsed);
    }

    private static List<Engine> Engines()
    {
        static byte[] Mask(Crop crop) => BattleInk.Isolate(crop.Bgra, crop.Width, crop.Height);
        return
        [
            new("win-prod", "Windows OCR on BattleInk.Isolate, 1x. The panel read the companion uses today.",
                crop => WinOcr.Read(Mask(crop), crop.Width, crop.Height).ToList()),
            new("win-raw-2x", "Windows OCR on the untouched crop, 2x.",
                crop => WinOcr.Read(Pixels.Scale(crop.Bgra, crop.Width, crop.Height, 2), crop.Width * 2, crop.Height * 2).ToList()),
            new("paddle-rows", "PaddleOCR v5 latin on the untouched crop, 1x, boxes joined into rows.",
                crop => Paddle.ReadRows(crop.Bgra, crop.Width, crop.Height, 1, "latin@1")),
            new("paddle-rows-2x", "PaddleOCR v5 latin on the untouched crop, 2x, boxes joined into rows.",
                crop => Paddle.ReadRows(crop.Bgra, crop.Width, crop.Height, 2, "latin@1")),
            new("paddle-mask", "PaddleOCR v5 latin on BattleInk.Isolate, 2x, rows.",
                crop => Paddle.ReadRows(Mask(crop), crop.Width, crop.Height, 2, "latin@1")),
        ];
    }
}
