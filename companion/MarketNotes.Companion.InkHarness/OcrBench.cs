using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// Loot crops through several OCR engines, scored against hand-checked lines.
///   --ocr-bench collect   copies every loot crop on disk into ocr-bench\crops (full frames are cut to LootRegion)
///   --ocr-bench run       reads each crop with every engine, writes report.txt, lines.tsv, and report.html
/// A crop's truth is crops\NAME.truth.txt, one notice line per row, as far as the line is visible in the crop.
/// Name plates and tooltips are not truth; only the white system notices are.
/// </summary>
static class OcrBench
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarketNotes.Companion");

    private static readonly string Bench = Path.Combine(Root, "ocr-bench");
    private static readonly string Crops = Path.Combine(Bench, "crops");

    private static readonly Regex Template = new(
        @"^(?:You have gained (?:an item in the (?:Etc|Use|Eqp|Equip|Setup|Set-up|Cash) tab \([^()]+\)|mesos \(\+[\d,]+\)|experience \(\+[\d,]+\))|Your (?:Etc|Use|Eqp|Equip|Setup|Set-up|Cash) inventory is full)\.?$");

    public static int Run(string step, string? only)
    {
        return step switch
        {
            "collect" => Collect(),
            "run" => Score(only),
            "throughput" => Throughput(only ?? "latin@1"),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("--ocr-bench collect | run [--engines a,b]");
        return 1;
    }

    private static int Collect()
    {
        Directory.CreateDirectory(Crops);
        var seen = new HashSet<string>(Directory.EnumerateFiles(Crops, "*.png").Select(path => Hash(Pixels.Load(path).Bgra)));
        var added = 0;
        var work = Path.Combine(Root, "ink-work");
        added += Take(Path.Combine(work, "loot-labeled"), "labeled", seen);
        added += Take(Path.Combine(work, "frames"), "video", seen);
        added += Take(Path.Combine(work, "loot-video"), "video2", seen);
        added += Take(Path.Combine(work, "loot-hunt", "ss"), "hunt", seen, name => name.StartsWith("loot_", StringComparison.Ordinal));
        added += Take(Path.Combine(Root, "samples", "loot"), "sample", seen);
        Console.WriteLine($"added {added}, {Directory.EnumerateFiles(Crops, "*.png").Count()} crops in {Crops}");
        return 0;
    }

    private static int Take(string dir, string prefix, HashSet<string> seen, Func<string, bool>? keep = null)
    {
        if (!Directory.Exists(dir)) return 0;
        var added = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "*.png").OrderBy(path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (keep != null && !keep(name)) continue;
            var image = Pixels.Load(path);
            var bgra = image.Bgra;
            var width = image.Width;
            var height = image.Height;
            if (width >= 1000)
            {
                var box = LootRegion.Bounds(width, height);
                bgra = Pixels.Crop(bgra, width, box.X, box.Y, box.Width, box.Height);
                width = box.Width;
                height = box.Height;
            }

            if (!seen.Add(Hash(bgra))) continue;
            var target = Path.Combine(Crops, prefix + "-" + name + ".png");
            Pixels.Save(target, bgra, width, height);
            var truth = Path.ChangeExtension(path, ".truth.txt");
            if (File.Exists(truth)) File.Copy(truth, Path.ChangeExtension(target, ".truth.txt"), overwrite: true);
            added++;
        }

        return added;
    }

    private sealed record Crop(string Name, string Path, byte[] Bgra, int Width, int Height, List<string>? Truth);

    private sealed record Engine(string Name, string About, Func<Crop, List<string>> Read);

    private sealed class Tally
    {
        public int Frames;
        public int TruthFrames;
        public int TruthLines;
        public int TruthChars;
        public int Edits;
        public int Exact;
        public int DropsWanted;
        public int DropHits;
        public int FalseDrops;
        public int TemplateLines;
        public int ParsedDrops;
        public long Ms;
        public long CpuMs;
    }

    private static int Score(string? only)
    {
        var crops = Directory.EnumerateFiles(Crops, "*.png")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Load)
            .ToList();
        if (crops.Count == 0)
        {
            Console.WriteLine("No crops. Run --ocr-bench collect first.");
            return 1;
        }

        var engines = Engines();
        if (!string.IsNullOrWhiteSpace(only))
        {
            var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            engines = engines.Where(engine => wanted.Contains(engine.Name)).ToList();
        }

        Console.WriteLine($"{crops.Count} crops, {crops.Count(crop => crop.Truth != null)} with truth, {engines.Count} engines");
        var reads = new Dictionary<(string Engine, string Crop), List<string>>();
        var tallies = new Dictionary<string, Tally>();
        foreach (var engine in engines)
        {
            var tally = new Tally();
            tallies[engine.Name] = tally;
            try
            {
                engine.Read(crops[0]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{engine.Name} did not start: {ex.Message}");
            }

            foreach (var crop in crops)
            {
                var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                List<string> lines;
                try
                {
                    lines = engine.Read(crop);
                }
                catch (Exception ex)
                {
                    lines = ["(failed: " + ex.GetType().Name + " " + ex.Message + ")"];
                }

                clock.Stop();
                tally.Ms += clock.ElapsedMilliseconds;
                tally.CpuMs += (long)(Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
                tally.Frames++;
                reads[(engine.Name, crop.Name)] = lines;
                Add(tally, crop, lines);
            }

            Console.WriteLine($"{engine.Name,-16} {Summary(tally)}");
        }

        var report = new StringBuilder();
        report.AppendLine($"OCR bench {DateTime.Now:yyyy-MM-dd HH:mm}  crops {crops.Count}, with truth {crops.Count(crop => crop.Truth != null)}");
        report.AppendLine("charErr = edits / truth chars, over the best-matching OCR line per truth line. exact = truth lines read letter for letter.");
        report.AppendLine("drops = truth pickups whose item and amount parsed right. false = parsed pickups no truth line has. clean = lines in the exact game wording, all crops.");
        report.AppendLine();
        foreach (var engine in engines)
        {
            report.AppendLine($"{engine.Name,-16} {Summary(tallies[engine.Name])}   {engine.About}");
        }

        File.WriteAllText(Path.Combine(Bench, "report.txt"), report.ToString());
        WriteLines(crops, engines, reads);
        WriteHtml(crops, engines, reads, tallies);
        Console.WriteLine();
        Console.WriteLine(report.ToString());
        Console.WriteLine(Path.Combine(Bench, "report.html"));
        return 0;
    }

    /// <summary>Parallel PaddleOCR readers, one thread and one model set each: reads per second, cores, and memory.</summary>
    private static int Throughput(string key)
    {
        var crops = Directory.EnumerateFiles(Crops, "sample-*.png").OrderBy(path => path, StringComparer.Ordinal).Take(40).Select(Load).ToList();
        if (crops.Count == 0) return Usage();
        var expected = crops.Select(crop => string.Join(" | ", Paddle.Read(crop.Bgra, crop.Width, crop.Height, 2, key))).ToList();
        Console.WriteLine($"{key}: {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB after one-at-a-time reads");
        var mismatches = 0;
        Parallel.For(0, crops.Count * 4, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var crop = crops[i % crops.Count];
            var got = string.Join(" | ", Paddle.Read(crop.Bgra, crop.Width, crop.Height, 2, key));
            if (got != expected[i % crops.Count]) Interlocked.Increment(ref mismatches);
        });
        Console.WriteLine($"one shared reader, 8 threads at once: {mismatches} of {crops.Count * 4} reads differed from the one-at-a-time read");
        foreach (var workers in new[] { 4, 8 })
        {
            var reads0 = 0;
            var cpuShared = Process.GetCurrentProcess().TotalProcessorTime;
            var sharedClock = Stopwatch.StartNew();
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
            {
                var i = w;
                while (sharedClock.ElapsedMilliseconds < 5000)
                {
                    var crop = crops[i++ % crops.Count];
                    Paddle.Read(crop.Bgra, crop.Width, crop.Height, 2, key);
                    Interlocked.Increment(ref reads0);
                }
            });
            sharedClock.Stop();
            var sharedCores = (Process.GetCurrentProcess().TotalProcessorTime - cpuShared).TotalMilliseconds / sharedClock.Elapsed.TotalMilliseconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"shared reader, {workers,2} threads: {reads0 / sharedClock.Elapsed.TotalSeconds,5:0.0} reads/s  {sharedCores,5:0.0} cores  {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),5} MB"));
        }

        return 0;
    }

    private static string Summary(Tally tally)
    {
        var err = tally.TruthChars == 0 ? 0 : 100.0 * tally.Edits / tally.TruthChars;
        var ms = tally.Frames == 0 ? 0 : tally.Ms / tally.Frames;
        var cpu = tally.Frames == 0 ? 0 : tally.CpuMs / tally.Frames;
        return string.Create(CultureInfo.InvariantCulture,
            $"charErr {err,5:0.0}%  exact {tally.Exact,3}/{tally.TruthLines,-3}  drops {tally.DropHits,3}/{tally.DropsWanted,-3}  false {tally.FalseDrops,3}  clean {tally.TemplateLines,4}  parsed {tally.ParsedDrops,4}  {ms,4} ms  cpu {cpu,4} ms");
    }

    private static Crop Load(string path)
    {
        var image = Pixels.Load(path);
        var truthPath = Path.ChangeExtension(path, ".truth.txt");
        List<string>? truth = null;
        if (File.Exists(truthPath))
        {
            truth = File.ReadAllLines(truthPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToList();
        }

        return new Crop(Path.GetFileNameWithoutExtension(path), path, image.Bgra, image.Width, image.Height, truth);
    }

    private static void Add(Tally tally, Crop crop, List<string> lines)
    {
        foreach (var line in lines)
        {
            if (Template.IsMatch(line.Trim())) tally.TemplateLines++;
            if (DropLines.Parse(line) != null) tally.ParsedDrops++;
        }

        if (crop.Truth == null) return;
        tally.TruthFrames++;
        var used = new bool[lines.Count];
        foreach (var want in crop.Truth)
        {
            tally.TruthLines++;
            tally.TruthChars += want.Length;
            var best = -1;
            var bestEdits = want.Length;
            for (var i = 0; i < lines.Count; i++)
            {
                if (used[i]) continue;
                var edits = Distance(Norm(want), Norm(lines[i]));
                if (edits >= bestEdits) continue;
                best = i;
                bestEdits = edits;
            }

            if (best >= 0) used[best] = true;
            tally.Edits += bestEdits;
            if (bestEdits == 0) tally.Exact++;
        }

        var pool = lines.Select(line => DropLines.Parse(line)).Where(drop => drop != null).Select(drop => drop!).ToList();
        foreach (var want in crop.Truth.Select(line => DropLines.Parse(line)).Where(drop => drop != null).Select(drop => drop!))
        {
            tally.DropsWanted++;
            var index = pool.FindIndex(got => got.ItemName.Equals(want.ItemName, StringComparison.OrdinalIgnoreCase) && got.Quantity == want.Quantity);
            if (index < 0) continue;
            pool.RemoveAt(index);
            tally.DropHits++;
        }

        tally.FalseDrops += pool.Count;
    }

    private static string Norm(string line) => Regex.Replace(line.Trim(), @"\s+", " ");

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    private static void WriteLines(List<Crop> crops, List<Engine> engines, Dictionary<(string, string), List<string>> reads)
    {
        var tsv = new StringBuilder("crop\tengine\tlines\n");
        foreach (var crop in crops)
        {
            if (crop.Truth != null) tsv.Append(crop.Name).Append("\ttruth\t").AppendJoin(" | ", crop.Truth).Append('\n');
            foreach (var engine in engines)
            {
                tsv.Append(crop.Name).Append('\t').Append(engine.Name).Append('\t')
                    .AppendJoin(" | ", reads[(engine.Name, crop.Name)].Select(line => line.Replace('\t', ' '))).Append('\n');
            }
        }

        File.WriteAllText(Path.Combine(Bench, "lines.tsv"), tsv.ToString());
    }

    private static void WriteHtml(List<Crop> crops, List<Engine> engines, Dictionary<(string, string), List<string>> reads, Dictionary<string, Tally> tallies)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><meta charset=utf-8><title>OCR bench</title><style>")
            .Append("body{font:13px/1.4 Segoe UI,sans-serif;background:#16181d;color:#ddd;margin:16px}")
            .Append("table{border-collapse:collapse}td,th{border:1px solid #333;padding:4px 8px;vertical-align:top;text-align:left}")
            .Append(".crop{margin:18px 0 6px;font-weight:600}.ok{color:#7ddc83}.near{color:#e6c35c}.bad{color:#ff7b72}.plain{color:#aaa}")
            .Append("img{image-rendering:pixelated;max-width:100%;border:1px solid #333}.truth{color:#9cc8ff}")
            .Append("</style><h2>OCR bench, ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("</h2>")
            .Append("<p>Green: a truth line read exactly. Yellow: within a few letters. Red: a parsed pickup no truth line has. Gray: anything else.</p><table><tr><th>engine</th><th>score</th><th>how</th></tr>");
        foreach (var engine in engines)
        {
            html.Append("<tr><td>").Append(WebUtility.HtmlEncode(engine.Name)).Append("</td><td><code>")
                .Append(WebUtility.HtmlEncode(Summary(tallies[engine.Name]))).Append("</code></td><td>")
                .Append(WebUtility.HtmlEncode(engine.About)).Append("</td></tr>");
        }

        html.Append("</table>");
        foreach (var crop in crops.OrderBy(crop => crop.Truth == null).ThenBy(crop => crop.Name, StringComparer.Ordinal))
        {
            html.Append("<div class=crop>").Append(WebUtility.HtmlEncode(crop.Name)).Append(crop.Truth == null ? " (no truth)" : "").Append("</div>")
                .Append("<img src=\"crops/").Append(Uri.EscapeDataString(crop.Name)).Append(".png\" width=").Append(crop.Width * 2).Append("><table>");
            if (crop.Truth != null)
            {
                html.Append("<tr><td>truth</td><td class=truth>").AppendJoin("<br>", crop.Truth.Select(WebUtility.HtmlEncode)).Append("</td></tr>");
            }

            foreach (var engine in engines)
            {
                html.Append("<tr><td>").Append(WebUtility.HtmlEncode(engine.Name)).Append("</td><td>");
                foreach (var line in reads[(engine.Name, crop.Name)])
                {
                    html.Append("<span class=").Append(Shade(line, crop.Truth)).Append('>').Append(WebUtility.HtmlEncode(line)).Append("</span><br>");
                }

                html.Append("</td></tr>");
            }

            html.Append("</table>");
        }

        File.WriteAllText(Path.Combine(Bench, "report.html"), html.ToString());
    }

    private static string Shade(string line, List<string>? truth)
    {
        if (truth == null) return Template.IsMatch(line.Trim()) ? "ok" : "plain";
        var norm = Norm(line);
        var best = truth.Select(want => Distance(Norm(want), norm)).DefaultIfEmpty(int.MaxValue).Min();
        if (best == 0) return "ok";
        if (best <= 4) return "near";
        var drop = DropLines.Parse(line);
        if (drop != null && !truth.Select(want => DropLines.Parse(want)).Any(want => want != null && want.ItemName == drop.ItemName && want.Quantity == drop.Quantity)) return "bad";
        return "plain";
    }

    private static string Hash(byte[] bgra)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in bgra)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static List<Engine> Engines()
    {
        static byte[] Notice(Crop crop) => LootInk.IsolateNotice(crop.Bgra, crop.Width, crop.Height);
        static (byte[] Bgra, int Width, int Height) Sheet(Crop crop, int scale)
        {
            var sheet = Pixels.BlackOnWhite(Notice(crop), crop.Width, crop.Height, whiteInk: true);
            return (Pixels.PadScale(sheet, crop.Width, crop.Height, 8, scale, 255), (crop.Width + 16) * scale, (crop.Height + 16) * scale);
        }

        return
        [
            new("win-prod", "Windows OCR on LootInk.IsolateNotice, 1x. What the companion does today.",
                crop => WinOcr.Read(Notice(crop), crop.Width, crop.Height).ToList()),
            new("win-raw", "Windows OCR on the untouched crop, 1x.",
                crop => WinOcr.Read(crop.Bgra, crop.Width, crop.Height).ToList()),
            new("win-raw-2x", "Windows OCR on the untouched crop, upscaled 2x.",
                crop => WinOcr.Read(Pixels.Scale(crop.Bgra, crop.Width, crop.Height, 2), crop.Width * 2, crop.Height * 2).ToList()),
            new("win-mask-3x", "Windows OCR on the notice mask as black on white, 3x.",
                crop => { var s = Sheet(crop, 3); return WinOcr.Read(s.Bgra, s.Width, s.Height).ToList(); }),
            new("paddle", "PaddleOCR v5 latin (RapidOcrNet, ONNX, CPU) on the untouched crop.",
                crop => Paddle.Read(crop.Bgra, crop.Width, crop.Height, 1)),
            new("paddle-2x", "PaddleOCR v5 latin on the untouched crop, upscaled 2x.",
                crop => Paddle.Read(crop.Bgra, crop.Width, crop.Height, 2)),
            new("paddle-3x", "PaddleOCR v5 latin on the untouched crop, upscaled 3x.",
                crop => Paddle.Read(crop.Bgra, crop.Width, crop.Height, 3)),
            new("paddle-en-2x", "PaddleOCR v5 detector with the English-only v5 recognizer, untouched crop, 2x.",
                crop => Paddle.Read(crop.Bgra, crop.Width, crop.Height, 2, "en")),
            new("paddle-v6-2x", "PaddleOCR v6 small detector and recognizer, untouched crop, 2x.",
                crop => Paddle.Read(crop.Bgra, crop.Width, crop.Height, 2, "v6")),
            new("paddle-2x-t1", "paddle-2x on one CPU thread.",
                crop => Paddle.Read(crop.Bgra, crop.Width, crop.Height, 2, "latin@1")),
            new("paddle-2x-t2", "paddle-2x on two CPU threads.",
                crop => Paddle.Read(crop.Bgra, crop.Width, crop.Height, 2, "latin@2")),
            new("paddle-mask", "PaddleOCR v5 latin on the notice mask as black on white, 2x.",
                crop => { var s = Sheet(crop, 2); return Paddle.Read(s.Bgra, s.Width, s.Height, 1); }),
            new("tess-mask-3x", "Tesseract 5 LSTM (tessdata_best eng) on the notice mask as black on white, 3x, block mode.",
                crop => { var s = Sheet(crop, 3); return Tess.Read(s.Bgra, s.Width, s.Height); }),
        ];
    }
}
