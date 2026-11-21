namespace MarketNotes.Companion.Scanning;

/// <summary>
/// One stat row of the battle analysis window, label on the left and value
/// on the right. The pixels are the ink mask, padded and enlarged for OCR.
/// Source coordinates are the tight value cell inside the original crop.
/// </summary>
public readonly record struct BattleColumnCrop(
    string Name,
    byte[] Pixels,
    int Width,
    int Height,
    int SourceX,
    int SourceY,
    int SourceWidth,
    int SourceHeight,
    byte[] LabelPixels,
    int LabelWidth,
    int LabelHeight);

/// <summary>
/// Splits the battle window by the ink itself. Each horizontal band is one
/// of Dmg, Mob, Exp, Meso, and Elapsed, and the gap in that band separates
/// the label from the value. Column order comes from the pixels, not from
/// the order Windows OCR would read the whole panel.
/// </summary>
public static class BattleColumns
{
    private static readonly string[] Names = ["Dmg", "Mob", "Exp", "Meso", "Elapsed"];

    /// <summary>
    /// The row name comes from the label cell. Position is only a hint.
    /// </summary>
    public static string? NameFromLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var word = text.ToLowerInvariant();
        if (word.Contains("mob")) return "Mob";
        if (word.Contains("dmg") || word.Contains("dam") || word.Contains("omg") || word.Contains("0mg")) return "Dmg";
        if (word.Contains("exp")) return "Exp";
        if (word.Contains("mes")) return "Meso";
        if (word.Contains("elap") || word.Contains("aps")) return "Elapsed";
        return null;
    }

    public static IReadOnlyList<BattleColumnCrop> Slice(byte[] bgra, int width, int height)
    {
        if (width < 40 || height < 30 || bgra.Length != width * height * 4) return [];
        var ink = BattleInk.Isolate(bgra, width, height);
        return SliceInk(ink, width, height);
    }

    /// <summary>
    /// Same row split as <see cref="Slice"/>, on a mask that is already black
    /// glyphs on white. The harness uses this to score a candidate mask
    /// without a second copy of the gutter logic.
    /// </summary>
    public static IReadOnlyList<BattleColumnCrop> SliceInk(byte[] ink, int width, int height)
    {
        if (width < 40 || height < 30 || ink.Length != width * height * 4) return [];
        var rows = InkRows(ink, width, height);
        var bands = Bands(rows, height);
        if (!SharedGutter(ink, width, bands, out var gapStart, out var gapEnd)) return [];
        var stats = new List<Band>();
        foreach (var band in bands)
        {
            if (!Cut(ink, width, band, gapStart, gapEnd, out var valueX, out var valueRight, out var valueInk, out var labelX, out var labelRight)) continue;
            if (labelRight - labelX > width * 34 / 100) continue;
            stats.Add(band with
            {
                ValueInk = valueInk,
                ValueX = valueX,
                ValueRight = valueRight,
                LabelX = labelX,
                LabelRight = labelRight,
            });
        }

        // "Battle Analysis" is much wider than Dmg/Mob/Exp/Meso. Counting it
        // as the first stat shifts the damage figure onto the Mob row.
        stats.RemoveAll(band => band.LabelRight > band.LabelX && band.LabelRight - band.LabelX > width * 34 / 100);
        if (stats.Count > Names.Length && stats[0].ValueInk * 3 < Median(stats))
        {
            stats.RemoveAt(0);
        }

        if (stats.Count > Names.Length) stats.RemoveRange(Names.Length, stats.Count - Names.Length);
        if (stats.Count < 4) return [];

        var crops = new List<BattleColumnCrop>(stats.Count);
        for (var i = 0; i < stats.Count; i++)
        {
            var band = stats[i];
            var x = band.ValueX;
            var y = band.Top;
            var cropWidth = Math.Max(1, band.ValueRight - x + 1);
            var cropHeight = Math.Max(1, band.Bottom - y + 1);
            var padded = Pad(ink, width, height, x, y, cropWidth, cropHeight, out var outWidth, out var outHeight);
            var labelWidth = Math.Max(0, band.LabelRight - band.LabelX + 1);
            byte[] labelPixels = [];
            var labelOutW = 0;
            var labelOutH = 0;
            if (labelWidth > 1)
            {
                labelPixels = Pad(ink, width, height, band.LabelX, y, labelWidth, cropHeight, out labelOutW, out labelOutH);
            }

            crops.Add(new BattleColumnCrop(Names[i], padded, outWidth, outHeight, x, y, cropWidth, cropHeight, labelPixels, labelOutW, labelOutH));
        }

        return crops;
    }

    private readonly record struct Band(int Top, int Bottom, int ValueInk, int ValueX = 0, int ValueRight = 0, int LabelX = 0, int LabelRight = 0);

    private static int[] InkRows(byte[] ink, int width, int height)
    {
        var rows = new int[height];
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (ink[row + x * 4] == 0) rows[y]++;
            }
        }

        return rows;
    }

    private static List<Band> Bands(int[] rows, int height)
    {
        var bands = new List<Band>();
        var top = -1;
        var ink = 0;
        for (var y = 0; y <= height; y++)
        {
            // A couple of leaf or border pixels must not glue two stat rows together.
            var on = y < height && rows[y] >= 8;
            if (on)
            {
                if (top < 0) top = y;
                ink += rows[y];
                continue;
            }

            if (top < 0) continue;
            if (y - top >= 2) bands.Add(new Band(top, y - 1, ink));
            top = -1;
            ink = 0;
        }

        for (var i = bands.Count - 1; i > 0; i--)
        {
            if (bands[i].Top - bands[i - 1].Bottom > 2) continue;
            bands[i - 1] = new Band(bands[i - 1].Top, bands[i].Bottom, bands[i - 1].ValueInk + bands[i].ValueInk);
            bands.RemoveAt(i);
        }

        return bands;
    }

    private static bool SharedGutter(byte[] ink, int width, List<Band> bands, out int gapStart, out int gapEnd)
    {
        gapStart = 0;
        gapEnd = 0;
        var bestStart = -1;
        var bestLength = 0;
        foreach (var band in bands)
        {
            var cols = new int[width];
            for (var y = band.Top; y <= band.Bottom; y++)
            {
                var row = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    if (ink[row + x * 4] == 0) cols[x]++;
                }
            }

            var from = width / 6;
            var to = width * 4 / 5;
            var run = -1;
            for (var x = from; x <= to; x++)
            {
                if (cols[x] == 0)
                {
                    if (run < 0) run = x;
                    continue;
                }

                if (run >= 0 && x - run > bestLength)
                {
                    bestStart = run;
                    bestLength = x - run;
                }

                run = -1;
            }
        }

        if (bestLength < Math.Max(8, width / 16)) return false;
        gapStart = bestStart;
        gapEnd = bestStart + bestLength;
        return true;
    }

    private static bool Cut(byte[] ink, int width, Band band, int gapStart, int gapEnd, out int valueX, out int valueRight, out int valueInk, out int labelX, out int labelRight)
    {
        valueX = gapEnd;
        valueRight = gapEnd;
        valueInk = 0;
        labelX = 0;
        labelRight = gapStart - 1;
        var labelInk = 0;
        for (var y = band.Top; y <= band.Bottom; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (ink[row + x * 4] != 0) continue;
                if (x < gapStart)
                {
                    if (labelInk == 0) labelX = x;
                    labelRight = x;
                    labelInk++;
                }
                else if (x >= gapEnd)
                {
                    if (valueInk == 0) valueX = x;
                    valueRight = x;
                    valueInk++;
                }
            }
        }

        return labelInk >= 8 && valueInk >= 4 && labelRight > labelX && valueRight > valueX;
    }

    private static int Median(List<Band> bands)
    {
        var amounts = bands.Select(band => band.ValueInk).OrderBy(amount => amount).ToArray();
        return amounts[amounts.Length / 2];
    }

    private static byte[] Pad(byte[] ink, int width, int height, int x, int y, int cropWidth, int cropHeight, out int outWidth, out int outHeight)
    {
        const int PadPx = 8;
        const int Scale = 2;
        outWidth = (cropWidth + PadPx * 2) * Scale;
        outHeight = (cropHeight + PadPx * 2) * Scale;
        var padded = new byte[outWidth * outHeight * 4];
        for (var i = 0; i < padded.Length; i += 4)
        {
            padded[i] = 255;
            padded[i + 1] = 255;
            padded[i + 2] = 255;
            padded[i + 3] = 255;
        }

        for (var row = 0; row < cropHeight; row++)
        {
            if (y + row < 0 || y + row >= height) continue;
            for (var col = 0; col < cropWidth; col++)
            {
                if (x + col < 0 || x + col >= width) continue;
                var source = ((y + row) * width + (x + col)) * 4;
                if (ink[source] != 0) continue;
                for (var sy = 0; sy < Scale; sy++)
                {
                    for (var sx = 0; sx < Scale; sx++)
                    {
                        var dest = (((row + PadPx) * Scale + sy) * outWidth + ((col + PadPx) * Scale + sx)) * 4;
                        padded[dest] = 0;
                        padded[dest + 1] = 0;
                        padded[dest + 2] = 0;
                        padded[dest + 3] = 255;
                    }
                }
            }
        }

        return padded;
    }
}
