using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// Scores ink masks with Windows OCR.
///   dotnet run --project companion/MarketNotes.Companion.InkHarness -c Release -- --video "C:\path\clip.mp4" --out "%LOCALAPPDATA%\MarketNotes.Companion\ink-compare"
/// Also reads --frames, --battle-crop, and %LOCALAPPDATA%\MarketNotes.Companion\samples\{battle,loot}.
/// Writes report.txt, detail.tsv, and side-by-side PNGs (raw | baseline mask | candidate).
/// </summary>
static class Program
{
    private const long DmgMin = 2_170_000;
    private const long DmgMax = 2_210_000;

    private static readonly (string Name, Func<byte[], int, int, byte[]> Mask, int PanelScale)[] BattleVariants =
    [
        ("baseline", Masks.BattleBaseline, 1),
        ("production", BattleInk.Isolate, 1),
        ("drop-tall", (p, w, h) => Masks.DropTall(Masks.BattleBaseline(p, w, h), w, h, 26, 4), 1),
        ("thin-2", (p, w, h) => Masks.KeepThin(Masks.BattleBaseline(p, w, h), w, h, 2), 1),
        ("edges", Masks.BattleEdges, 1),
        ("baseline-x3", Masks.BattleBaseline, 3),
        ("edges-x3", Masks.BattleEdges, 3),
    ];

    /// <summary>
    /// raw is what the companion does today: white glyphs on black, no scale.
    /// Every other loot variant is black text on white. Lines splits on the
    /// mask's rows. Tight crops each row to its ink before OCR.
    /// </summary>
    private static readonly LootVariant[] LootVariants =
    [
        new("raw", LootInk.Isolate, 1, false, false, false, 6),
        new("notice1", LootInk.IsolateNotice, 1, false, false, false, 6),
        new("notice3", Masks.LootNotice, 3, false, true, false, 6),
        new("notice4", Masks.LootNotice, 4, false, true, false, 6),
        new("notice-short", Masks.LootNotice, 3, true, true, true, 3),
        new("baseline-short", Masks.LootBaseline, 3, true, true, true, 3),
    ];

    public static int Main(string[] args)
    {
        var replay = Arg(args, "--replay-loot");
        if (replay != null) return LootReplay.Run(replay, Arg(args, "--from"), Arg(args, "--to"));
        var captureBench = Arg(args, "--capture-bench");
        if (captureBench != null) return CaptureBench.Run(captureBench);
        var bench = Arg(args, "--ocr-bench");
        if (bench != null) return OcrBench.Run(bench, Arg(args, "--engines"));
        var battleBench = Arg(args, "--battle-bench");
        if (battleBench != null)
        {
            if (Arg(args, "--crops") is { } crops) BattleBench.Crops = crops;
            return BattleBench.Run(battleBench, Arg(args, "--engines"));
        }
        if (Has(args, "--stat-grab")) return StatGrab.Run();
        var ocrPng = Arg(args, "--ocr-png");
        if (ocrPng != null)
        {
            if (!WinOcr.Available) { Console.WriteLine("Windows OCR is not available."); return 1; }
            var img = Pixels.Load(ocrPng);
            foreach (var line in WinOcr.Read(img.Bgra, img.Width, img.Height)) Console.WriteLine(line);
            return 0;
        }
        var battleGrab = Arg(args, "--battle-grab");
        if (battleGrab != null) return BattleGrab.Run(battleGrab);
        var battleFrames = Arg(args, "--battle-frames");
        if (battleFrames != null) return BattleGrab.FromFrames(battleFrames);
        var video = Arg(args, "--video");
        var framesDir = Arg(args, "--frames");
        var battleCrop = Arg(args, "--battle-crop");
        var outDir = Arg(args, "--out") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketNotes.Companion",
            "ink-compare");
        var part = Arg(args, "--part") ?? "all";
        Directory.CreateDirectory(outDir);

        if (!WinOcr.Available)
        {
            Console.WriteLine("Windows OCR is not available.");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(video))
        {
            framesDir = Path.Combine(outDir, "frames");
            Extract(video, framesDir);
        }

        var full = new List<Shot>();
        var battles = new List<Shot>();
        var loots = new List<Shot>();
        if (!string.IsNullOrWhiteSpace(framesDir) && Directory.Exists(framesDir))
        {
            foreach (var path in Directory.EnumerateFiles(framesDir, "*.png").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                AddFile(path, full, battles, loots);
            }
        }

        var samples = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarketNotes.Companion", "samples");
        TakeSamples(Path.Combine(samples, "battle"), battles);
        TakeSamples(Path.Combine(samples, "loot"), loots);
        if (!string.IsNullOrWhiteSpace(battleCrop) && File.Exists(battleCrop))
        {
            var shot = LoadShot(battleCrop);
            battles.Add(shot with { Kind = ShotKind.Battle });
        }

        Console.WriteLine($"frames {full.Count}, battle crops {battles.Count}, loot crops {loots.Count}");
        var detail = new StringBuilder();
        detail.AppendLine("kind\tvariant\tname\tpath\tfields\tgate\tdmg\tmob\texp\tmeso\telapsed\ttext");
        var report = new StringBuilder();

        if (part == "ocr" && args.Length > 0)
        {
            var file = args[^1];
            var image = Pixels.Load(file);
            Console.WriteLine(string.Join(" | ", WinOcr.Read(image.Bgra, image.Width, image.Height)));
            var stripW = Math.Min(90, image.Width);
            var strip = Pixels.Crop(image.Bgra, image.Width, image.Width - stripW, 0, stripW, image.Height);
            var big = Pixels.PadScale(strip, stripW, image.Height, 8, 3, 255);
            Console.WriteLine("strip " + string.Join(" | ", WinOcr.Read(big, (stripW + 16) * 3, (image.Height + 16) * 3)));
            return 0;
        }

        if (part == "probe")
        {
            Probe(full, battles, outDir);
            return 0;
        }

        if (part is "all" or "battle")
        {
            var panels = new List<Shot>();
            foreach (var shot in full)
            {
                var hit = BattleWindowFinder.FindHit(shot.Bgra, shot.Width, shot.Height, shot.Width * 4);
                if (hit == null)
                {
                    Console.WriteLine($"no battle window in {shot.Name}");
                    continue;
                }

                var box = hit.Value.Window;
                var crop = Pixels.Crop(shot.Bgra, shot.Width, box.X, box.Y, box.Width, box.Height);
                panels.Add(new Shot(shot.Name, crop, box.Width, box.Height, ShotKind.Battle, shot.SourcePath));
                Console.WriteLine($"battle {shot.Name} {box.Width}x{box.Height} at {box.X},{box.Y}");
            }

            panels.AddRange(battles);
            report.AppendLine(ScoreBattle(panels, outDir, detail));
        }

        if (part is "all" or "loot")
        {
            var crops = new List<Shot>();
            foreach (var shot in full)
            {
                var box = LootRegion.Bounds(shot.Width, shot.Height);
                var crop = Pixels.Crop(shot.Bgra, shot.Width, box.X, box.Y, box.Width, box.Height);
                crops.Add(new Shot(shot.Name, crop, box.Width, box.Height, ShotKind.Loot, shot.SourcePath));
            }

            crops.AddRange(loots);
            report.AppendLine(ScoreLoot(crops, "official", outDir, detail));

            if (full.Count > 0 && part == "loot-tall")
            {
                var tall = new List<Shot>();
                foreach (var shot in full)
                {
                    var box = Bounds(shot.Width, shot.Height, 0.62, 0.60, 1.0, 0.90);
                    var crop = Pixels.Crop(shot.Bgra, shot.Width, box.X, box.Y, box.Width, box.Height);
                    tall.Add(new Shot(shot.Name, crop, box.Width, box.Height, ShotKind.Loot, shot.SourcePath));
                }

                report.AppendLine(ScoreLoot(tall, "tall-edge", outDir, detail, images: false));
            }
        }

        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
        File.WriteAllText(Path.Combine(outDir, "detail.tsv"), detail.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine(outDir);
        return 0;
    }

    private static void Probe(List<Shot> full, List<Shot> battles, string outDir)
    {
        var dir = Path.Combine(outDir, "probe");
        Directory.CreateDirectory(dir);
        foreach (var shot in full.Where(shot => shot.Name is "f001" or "f008" or "f015"))
        {
            var hit = BattleWindowFinder.FindHit(shot.Bgra, shot.Width, shot.Height, shot.Width * 4);
            if (hit == null) continue;
            var box = hit.Value.Window;
            var crop = Pixels.Crop(shot.Bgra, shot.Width, box.X, box.Y, box.Width, box.Height);
            DumpBattle(shot.Name, crop, box.Width, box.Height, dir);
            var margin = Bounds(shot.Width, shot.Height, 0.88, 0.55, 1.0, 0.90);
            var edge = Pixels.Crop(shot.Bgra, shot.Width, margin.X, margin.Y, margin.Width, margin.Height);
            DumpLoot(shot.Name + "-margin", edge, margin.Width, margin.Height, dir);
            var official = LootRegion.Bounds(shot.Width, shot.Height);
            var loot = Pixels.Crop(shot.Bgra, shot.Width, official.X, official.Y, official.Width, official.Height);
            DumpLoot(shot.Name + "-official", loot, official.Width, official.Height, dir);
        }

        foreach (var shot in battles)
        {
            DumpBattle(shot.Name, shot.Bgra, shot.Width, shot.Height, dir);
        }
    }

    private static void DumpBattle(string name, byte[] crop, int width, int height, string dir)
    {
        var mask = Masks.BattleBaseline(crop, width, height);
        Pixels.Save(Path.Combine(dir, name + "-mask.png"), mask, width, height);
        var columns = BattleColumns.SliceInk(mask, width, height);
        Console.WriteLine($"PROBE {name} {width}x{height} columns {columns.Count}");
        foreach (var column in columns)
        {
            var value = Join(WinOcr.Read(column.Pixels, column.Width, column.Height));
            var label = column.LabelWidth > 1 ? Join(WinOcr.Read(column.LabelPixels, column.LabelWidth, column.LabelHeight)) : "";
            Console.WriteLine($"  {column.Name} label='{label}' value='{value}' src {column.SourceX},{column.SourceY} {column.SourceWidth}x{column.SourceHeight}");
            Pixels.Save(Path.Combine(dir, $"{name}-{column.Name}-value.png"), column.Pixels, column.Width, column.Height);
            if (column.LabelWidth > 1)
            {
                Pixels.Save(Path.Combine(dir, $"{name}-{column.Name}-label.png"), column.LabelPixels, column.LabelWidth, column.LabelHeight);
            }
        }

        var panel = string.Join(" | ", WinOcr.Read(mask, width, height));
        var big = Pixels.PadScale(mask, width, height, 10, 3, 255);
        var panel3 = string.Join(" | ", WinOcr.Read(big, (width + 20) * 3, (height + 20) * 3));
        Console.WriteLine("  panel: " + panel);
        Console.WriteLine("  panel3: " + panel3);
    }

    private static void DumpLoot(string name, byte[] crop, int width, int height, string dir)
    {
        Pixels.Save(Path.Combine(dir, name + ".png"), crop, width, height);
        foreach (var variant in LootVariants)
        {
            var mask = variant.Mask(crop, width, height);
            var prepared = PrepareLoot(mask, width, height, variant);
            var lines = variant.Lines
                ? ReadBands(prepared.Pixels, prepared.Width, prepared.Height, variant.Tight, variant.MinRows)
                : WinOcr.Read(prepared.Pixels, prepared.Width, prepared.Height);
            Console.WriteLine($"  loot {name} {variant.Name}: {string.Join(" | ", lines)}");
            if (variant.Name == "notice3")
            {
                Pixels.Save(Path.Combine(dir, name + "-notice.png"), mask, width, height);
                var stripW = Math.Min(width, 130);
                var strip = Pixels.Crop(mask, width, width - stripW, 0, stripW, height);
                var sheet = Pixels.BlackOnWhite(strip, stripW, height, whiteInk: true);
                var grown = Pixels.PadScale(sheet, stripW, height, 12, 4, 255);
                var stripText = string.Join(" | ", WinOcr.Read(grown, (stripW + 24) * 4, (height + 24) * 4));
                var ink = 0;
                for (var i = 0; i < grown.Length; i += 4) if (grown[i] == 0) ink++;
                Pixels.Save(Path.Combine(dir, name + "-strip.png"), grown, (stripW + 24) * 4, (height + 24) * 4);
                Console.WriteLine("  STRIP " + name + " ink=" + ink + ": " + stripText);
            }
        }
    }

    private static string ScoreBattle(List<Shot> shots, string outDir, StringBuilder detail)
    {
        var table = new StringBuilder();
        table.AppendLine("BATTLE  frames=" + shots.Count);
        table.AppendLine("variant\tframes\tfields\tall5\tgate\tlabelPath");
        if (shots.Count == 0) return table.ToString();
        var shown = 0;
        foreach (var variant in BattleVariants)
        {
            var fields = 0;
            var all5 = 0;
            var gate = 0;
            var labeled = 0;
            foreach (var shot in shots)
            {
                var mask = variant.Mask(shot.Bgra, shot.Width, shot.Height);
                var read = ReadBattle(mask, shot.Width, shot.Height, variant.PanelScale);
                var parsed = BattleText.Parse(read.Text);
                var score = FieldScore(parsed);
                fields += score;
                if (score == 5) all5++;
                if (parsed.FrameGood && score >= 4) gate++;
                if (read.FromColumns) labeled++;
                detail.Append("battle\t").Append(variant.Name).Append('\t').Append(shot.Name).Append('\t')
                    .Append(read.FromColumns ? "columns" : "panel").Append('\t')
                    .Append(score).Append('\t').Append(parsed.FrameGood ? "good" : parsed.FrameReason).Append('\t')
                    .Append(parsed.Damage?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\t')
                    .Append(parsed.Kills?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\t')
                    .Append(parsed.Exp?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\t')
                    .Append(parsed.Meso?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\t')
                    .Append(parsed.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\t')
                    .Append(read.Text.Replace('\t', ' ').Replace('\n', ' ')).AppendLine();
                if (variant.Name == "baseline" && shown < 2)
                {
                    Pixels.Save(Path.Combine(outDir, "raw", "battle-" + Safe(shot.Name) + ".png"), shot.Bgra, shot.Width, shot.Height);
                    shown++;
                }

                if (shown <= 2 && variant.Name is "edges" or "drop-tall" or "thin-2" or "baseline-x3")
                {
                    var baseline = Masks.BattleBaseline(shot.Bgra, shot.Width, shot.Height);
                    Pixels.SaveTrio(
                        Path.Combine(outDir, "images", "battle-" + Safe(shot.Name) + "-" + variant.Name + ".png"),
                        shot.Bgra,
                        baseline,
                        mask,
                        shot.Width,
                        shot.Height);
                }
            }

            table.Append(variant.Name).Append('\t').Append(shots.Count).Append('\t')
                .Append(fields).Append('/').Append(shots.Count * 5).Append('\t')
                .Append(all5).Append('\t').Append(gate).Append('\t').Append(labeled).Append('/').Append(shots.Count).AppendLine();
            Console.WriteLine("battle " + variant.Name + " " + fields + "/" + (shots.Count * 5));
        }

        return table.ToString();
    }

    private static string ScoreLoot(List<Shot> shots, string region, string outDir, StringBuilder detail, bool images = true)
    {
        var table = new StringBuilder();
        table.AppendLine("LOOT " + region + "  frames=" + shots.Count);
        table.AppendLine("variant\tframes\ttextFrames\tgainPhrase\tparsed\ttruthHits\ttruthPossible\tmsPerFrame");
        if (shots.Count == 0) return table.ToString();
        var truthPossible = 0;
        foreach (var shot in shots)
        {
            var truth = LoadTruth(shot.SourcePath);
            var possible = truth.Count(line => DropLines.Parse(line) != null);
            truthPossible += possible;
            if (truth.Count > 0)
            {
                Console.WriteLine("truth " + shot.Name + " lines " + truth.Count + " parseable " + possible);
                foreach (var line in truth)
                {
                    Console.WriteLine("  " + DropLines.Explain(line) + " :: " + line);
                }
            }
        }

        foreach (var variant in LootVariants)
        {
            var textFrames = 0;
            var phrases = 0;
            var parsed = 0;
            var hits = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var shot in shots)
            {
                var mask = variant.Mask(shot.Bgra, shot.Width, shot.Height);
                var prepared = PrepareLoot(mask, shot.Width, shot.Height, variant);
                var lines = variant.Lines
                    ? ReadBands(prepared.Pixels, prepared.Width, prepared.Height, variant.Tight, variant.MinRows)
                    : WinOcr.Read(prepared.Pixels, prepared.Width, prepared.Height);
                if (lines.Count > 0) textFrames++;
                foreach (var line in lines)
                {
                    if (GainPhrase(line)) phrases++;
                    if (DropLines.Parse(line) != null) parsed++;
                }

                var truth = LoadTruth(shot.SourcePath);
                var frameHits = TruthHits(lines, truth);
                hits += frameHits;
                detail.Append("loot-").Append(region).Append('\t').Append(variant.Name).Append('\t').Append(shot.Name)
                    .Append('\t').Append(frameHits).Append('\t')
                    .Append(string.Join(" | ", lines).Replace('\t', ' ')).AppendLine();
                if (images && variant.Name == "raw")
                {
                    Pixels.Save(Path.Combine(outDir, "raw", "loot-" + Safe(shot.Name) + ".png"), shot.Bgra, shot.Width, shot.Height);
                }

                if (images && variant.Name is "notice3")
                {
                    var baseline = Masks.LootBaseline(shot.Bgra, shot.Width, shot.Height);
                    Pixels.SaveTrio(
                        Path.Combine(outDir, "images", "loot-" + region + "-" + Safe(shot.Name) + "-" + variant.Name + ".png"),
                        shot.Bgra,
                        baseline,
                        mask,
                        shot.Width,
                        shot.Height);
                }
            }

            clock.Stop();
            var ms = shots.Count == 0 ? 0 : clock.ElapsedMilliseconds / shots.Count;
            table.Append(variant.Name).Append('\t').Append(shots.Count).Append('\t')
                .Append(textFrames).Append('\t').Append(phrases).Append('\t').Append(parsed).Append('\t')
                .Append(hits).Append('\t').Append(truthPossible).Append('\t').Append(ms).AppendLine();
            Console.WriteLine("loot " + region + " " + variant.Name + " phrase " + phrases + " parsed " + parsed + " hits " + hits + "/" + truthPossible + " text " + textFrames + " " + ms + "ms");
        }

        return table.ToString();
    }

    private readonly record struct Prepared(byte[] Pixels, int Width, int Height);

    private static Prepared PrepareLoot(byte[] mask, int width, int height, LootVariant variant)
    {
        var page = variant.Invert ? Pixels.BlackOnWhite(mask, width, height, whiteInk: true) : mask;
        if (variant.Scale <= 1) return new Prepared(page, width, height);
        var fill = variant.Invert ? (byte)255 : (byte)0;
        var scaled = Pixels.PadScale(page, width, height, 8, variant.Scale, fill);
        return new Prepared(scaled, (width + 16) * variant.Scale, (height + 16) * variant.Scale);
    }

    private static List<string> LoadTruth(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        var side = Path.ChangeExtension(path, ".truth.txt");
        if (!File.Exists(side)) return [];
        return File.ReadAllLines(side)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }

    /// <summary>Parsed OCR lines that match a ground-truth line's item and quantity. Each truth line is used once.</summary>
    private static int TruthHits(IReadOnlyList<string> ocr, IReadOnlyList<string> truth)
    {
        var pool = new List<ParsedDrop>();
        foreach (var line in ocr)
        {
            var parsed = DropLines.Parse(line);
            if (parsed != null) pool.Add(parsed);
        }

        var hits = 0;
        foreach (var line in truth)
        {
            var want = DropLines.Parse(line);
            if (want == null) continue;
            var index = pool.FindIndex(got =>
                got.ItemName.Equals(want.ItemName, StringComparison.OrdinalIgnoreCase)
                && got.Quantity == want.Quantity);
            if (index < 0) continue;
            pool.RemoveAt(index);
            hits++;
        }

        return hits;
    }

    private readonly record struct BattleRead(string Text, bool FromColumns);

    private static BattleRead ReadBattle(byte[] mask, int width, int height, int panelScale)
    {
        var columns = BattleColumns.SliceInk(mask, width, height);
        if (columns.Count >= 4)
        {
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                var valueText = Join(WinOcr.Read(column.Pixels, column.Width, column.Height));
                var labelText = column.LabelWidth > 1
                    ? Join(WinOcr.Read(column.LabelPixels, column.LabelWidth, column.LabelHeight))
                    : "";
                var name = BattleColumns.NameFromLabel(labelText);
                if (name == null) continue;
                if (name == "Mob" && Regex.IsMatch(valueText, @"\d\.\d+\s*M\b", RegexOptions.IgnoreCase)) continue;
                if (!named.ContainsKey(name) || named[name].Length == 0) named[name] = valueText;
            }

            var built = new List<string>();
            foreach (var name in new[] { "Dmg", "Mob", "Exp", "Meso", "Elapsed" })
            {
                if (!named.TryGetValue(name, out var value)) continue;
                built.Add(name + ": " + value);
            }

            if (named.TryGetValue("Dmg", out var dmg) && dmg.Any(char.IsDigit)
                && named.TryGetValue("Mob", out var mob) && mob.Any(char.IsDigit)
                && named.TryGetValue("Exp", out var exp) && exp.Any(char.IsDigit))
            {
                return new BattleRead(string.Join("\n", built), true);
            }
        }

        var panel = mask;
        var panelW = width;
        var panelH = height;
        if (panelScale > 1)
        {
            panel = Pixels.PadScale(mask, width, height, 8, panelScale, 255);
            panelW = (width + 16) * panelScale;
            panelH = (height + 16) * panelScale;
        }

        return new BattleRead(string.Join("\n", WinOcr.Read(panel, panelW, panelH)), false);
    }

    private static List<string> ReadBands(byte[] page, int width, int height, bool tight, int minRows)
    {
        var rows = new int[height];
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (page[row + x * 4] == 0) rows[y]++;
            }
        }

        var lines = new List<string>();
        var top = -1;
        for (var y = 0; y <= height; y++)
        {
            var on = y < height && rows[y] >= 4;
            if (on)
            {
                if (top < 0) top = y;
                continue;
            }

            if (top < 0) continue;
            if (y - top >= minRows)
            {
                var y0 = Math.Max(0, top - 4);
                var y1 = Math.Min(height, y + 4);
                var x0 = 0;
                var x1 = width;
                if (tight)
                {
                    x0 = width;
                    x1 = 0;
                    for (var yy = y0; yy < y1; yy++)
                    {
                        var row = yy * width * 4;
                        for (var x = 0; x < width; x++)
                        {
                            if (page[row + x * 4] != 0) continue;
                            if (x < x0) x0 = x;
                            if (x + 1 > x1) x1 = x + 1;
                        }
                    }

                    if (x1 > x0)
                    {
                        x0 = Math.Max(0, x0 - 8);
                        x1 = Math.Min(width, x1 + 8);
                    }
                    else
                    {
                        x0 = 0;
                        x1 = width;
                    }
                }

                var bandW = x1 - x0;
                var bandH = y1 - y0;
                var band = Pixels.Crop(page, width, x0, y0, bandW, bandH);
                lines.AddRange(WinOcr.Read(band, bandW, bandH));
            }

            top = -1;
            if (lines.Count >= 16) break;
        }

        return lines;
    }

    private static int FieldScore(BattleSnapshot shot)
    {
        var score = 0;
        if (shot.Damage is >= 2_160_000 and <= 2_250_000) score++;
        if (shot.Kills is >= 470 and <= 510) score++;
        if (shot.Exp is >= 88_000 and <= 94_000) score++;
        if (shot.Meso is >= 46_000 and <= 51_000) score++;
        if (shot.DurationMs is >= 82 * 60 * 1000L and <= (82 * 60 + 40) * 1000L) score++;
        return score;
    }

    private static bool GainPhrase(string line) =>
        Regex.IsMatch(line, @"you have gained|obtained|picked up", RegexOptions.IgnoreCase);

    private static string Join(IReadOnlyList<string> lines) =>
        string.Join(" ", lines.Select(line => line.Trim()).Where(line => line.Length > 0));

    private static void Extract(string video, string framesDir)
    {
        Directory.CreateDirectory(framesDir);
        if (Directory.EnumerateFiles(framesDir, "*.png").Any()) return;
        var ffmpeg = FindFfmpeg();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -i \"{video}\" -vf fps=1/2 \"{Path.Combine(framesDir, "f%03d.png")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg exited " + process.ExitCode);
    }

    private static string FindFfmpeg()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(root))
        {
            var hit = Directory.EnumerateFiles(root, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return hit;
        }

        return "ffmpeg";
    }

    private static void AddFile(string path, List<Shot> full, List<Shot> battles, List<Shot> loots)
    {
        var shot = LoadShot(path);
        var name = shot.Name;
        if (name.Contains("loot", StringComparison.OrdinalIgnoreCase)) loots.Add(shot with { Kind = ShotKind.Loot });
        else if (name.Contains("battle", StringComparison.OrdinalIgnoreCase) || shot.Width < 900) battles.Add(shot with { Kind = ShotKind.Battle });
        else full.Add(shot);
    }

    private static void TakeSamples(string dir, List<Shot> into)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.EnumerateFiles(dir, "*.png").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            into.Add(LoadShot(path));
        }
    }

    private static Shot LoadShot(string path)
    {
        var image = Pixels.Load(path);
        return new Shot(Path.GetFileNameWithoutExtension(path), image.Bgra, image.Width, image.Height, ShotKind.Full, path);
    }

    private static PixelRect Bounds(int width, int height, double left, double top, double right, double bottom)
    {
        var x = Math.Clamp((int)(width * left), 0, Math.Max(0, width - 1));
        var y = Math.Clamp((int)(height * top), 0, Math.Max(0, height - 1));
        var edge = Math.Clamp((int)Math.Ceiling(width * right), x + 1, width);
        var floor = Math.Clamp((int)Math.Ceiling(height * bottom), y + 1, height);
        return new PixelRect(x, y, edge - x, floor - y);
    }

    private static bool Has(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }

    private static string Safe(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        return new string(chars);
    }

    private enum ShotKind { Full, Battle, Loot }

    private readonly record struct Shot(string Name, byte[] Bgra, int Width, int Height, ShotKind Kind, string SourcePath);

    private readonly record struct LootVariant(
        string Name,
        Func<byte[], int, int, byte[]> Mask,
        int Scale,
        bool Lines,
        bool Invert,
        bool Tight,
        int MinRows);
}
