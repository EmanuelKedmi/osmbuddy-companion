namespace MarketNotes.Companion.Scanning;

/// <summary>
/// A hired-merchant row stays on the list after it sells. The sold row's
/// fill is the same pink, only paler. A row that leaves the list is a
/// closed shop or a scroll, not a sale.
/// </summary>
public static class ShopRowTone
{
    public const double PaleGap = 12;

    public static void Mark(byte[] bgra, int width, int height, int stride, IList<FmVisibleRow> rows)
    {
        foreach (var row in rows)
        {
            row.Sold = false;
            row.Covered = false;
        }
        if (rows.Count == 0 || width < 8 || height < rows.Count * 4 || stride < width * 4) return;

        var x0 = width * 55 / 100;
        var bandHeight = height / (double)rows.Count;
        var tone = new double[rows.Count];
        var pink = new double[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var y0 = (int)(i * bandHeight);
            var y1 = Math.Max(y0 + 1, (int)((i + 1) * bandHeight));
            var sample = LowerQuartile(bgra, stride, x0, width, y0, Math.Min(height, y1));
            tone[i] = sample.Lum;
            pink[i] = sample.RedBlue;
        }

        var dark = double.PositiveInfinity;
        var darkPink = double.NaN;
        for (var i = 0; i < tone.Length; i++)
        {
            if (double.IsNaN(tone[i]) || tone[i] >= dark) continue;
            dark = tone[i];
            darkPink = pink[i];
        }

        if (double.IsPositiveInfinity(dark)) return;
        for (var i = 0; i < rows.Count; i++)
        {
            var y0 = (int)(i * bandHeight);
            var y1 = Math.Max(y0 + 1, (int)((i + 1) * bandHeight));
            rows[i].Covered = HasCursor(bgra, stride, width, y0, Math.Min(height, y1));
            var samePink = !double.IsNaN(pink[i]) && !double.IsNaN(darkPink) && Math.Abs(pink[i] - darkPink) <= 14;
            rows[i].Sold = !rows[i].Covered && samePink && !double.IsNaN(tone[i]) && tone[i] >= dark + PaleGap;
        }
    }

    private readonly record struct ToneSample(double Lum, double RedBlue);

    private static ToneSample LowerQuartile(byte[] bgra, int stride, int x0, int x1, int y0, int y1)
    {
        var values = new List<(int Lum, int RedBlue)>();
        for (var y = y0; y < y1; y++)
        {
            var row = y * stride;
            for (var x = x0; x < x1; x++)
            {
                var i = row + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var lum = (r + g + b) / 3;
                if (lum is < 140 or > 240) continue;
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max - min > 40) continue;
                values.Add((lum, r - b));
            }
        }

        if (values.Count < 8) return new ToneSample(double.NaN, double.NaN);
        values.Sort((left, right) => left.Lum.CompareTo(right.Lum));
        var lower = Math.Max(1, values.Count / 2);
        var redBlue = 0.0;
        for (var i = 0; i < lower; i++) redBlue += values[i].RedBlue;
        return new ToneSample(values[values.Count / 4].Lum, redBlue / lower);
    }

    /// <summary>
    /// The Windows pointer is a dark arrow with a white edge. Item text is
    /// dark on the pink row and does not have that white edge.
    /// </summary>
    private static bool HasCursor(byte[] bgra, int stride, int width, int y0, int y1)
    {
        var x0 = Math.Max(0, width / 10);
        var x1 = width * 65 / 100;
        var winW = 16;
        var winH = Math.Min(22, y1 - y0);
        if (x1 - x0 < winW || winH < 8) return false;
        for (var y = y0; y + winH <= y1; y += 3)
        {
            for (var x = x0; x + winW <= x1; x += 3)
            {
                var dark = 0;
                var light = 0;
                for (var dy = 0; dy < winH; dy++)
                {
                    var row = (y + dy) * stride;
                    for (var dx = 0; dx < winW; dx++)
                    {
                        var i = row + (x + dx) * 4;
                        var lum = (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
                        if (lum < 45) dark++;
                        else if (lum > 230) light++;
                    }
                }

                if (dark >= 18 && light >= 8) return true;
            }
        }

        return false;
    }
}
