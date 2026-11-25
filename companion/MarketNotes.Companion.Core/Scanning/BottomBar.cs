namespace MarketNotes.Companion.Scanning;

/// <summary>
/// The bottom status bar. <see cref="Top"/> is the name plate's top edge in
/// frame pixels and <see cref="Bottom"/> is one past its last row.
/// <see cref="ChromeTop"/> is the top of the light input row that sits on
/// that plate. Loot notices are above <see cref="ChromeTop"/>.
/// <see cref="Measured"/> is false when the bar could not be seen and the
/// old fractions still apply.
/// </summary>
public readonly record struct BottomBar(int Top, int Bottom, bool Measured, int ChromeTop)
{
    public BottomBar(int top, int bottom, bool measured) : this(top, bottom, measured, top)
    {
    }

    public int Height => Math.Max(1, Bottom - Top);

    public static BottomBar Fallback(int height)
    {
        var top = (int)(height * 0.90);
        if (top < 0) top = 0;
        if (top >= height) top = Math.Max(0, height - 1);
        return new BottomBar(top, height, false);
    }
}

/// <summary>
/// Measures the status bar from the frame. Its pixel height shrinks when the
/// client is short, so a fixed fraction of the window misses the name plate
/// and the loot lines that sit just above the bar.
/// </summary>
public static class BottomBarFinder
{
    public static BottomBar Measure(byte[] bgra, int width, int height, int stride)
    {
        var fallback = BottomBar.Fallback(height);
        if (width < 40 || height < 40 || stride < width * 4 || bgra.Length < stride * height)
        {
            return fallback;
        }

        var bottom = height - 1;
        var blackLimit = Math.Max(0, height - Math.Max(8, height / 12));
        while (bottom > blackLimit && RowBlack(bgra, width, stride, bottom)) bottom--;

        if (TryPlateBelowInput(bgra, width, stride, height, bottom, out var plated))
        {
            return plated;
        }

        if (!ChromeRow(bgra, width, stride, bottom, out var baseMedian))
        {
            return fallback;
        }

        var limit = Math.Max(0, bottom - Math.Max(24, height / 4));
        var top = bottom;
        while (top - 1 >= limit && ChromeRow(bgra, width, stride, top - 1, out var median) && Math.Abs(median - baseMedian) <= 28)
        {
            top--;
        }

        var barHeight = bottom - top + 1;
        var minHeight = Math.Max(16, height / 40);
        var maxHeight = Math.Max(minHeight + 8, height / 5);
        if (barHeight < minHeight || barHeight > maxHeight)
        {
            return fallback;
        }

        return new BottomBar(top, bottom + 1, true);
    }

    /// <summary>
    /// The chat field is a tall near-white band directly above the name plate.
    /// The plate under it is the status bar. Loot lines sit above the field,
    /// so a fixed bottom tenth of a tall window lands on the field instead.
    /// </summary>
    private static bool TryPlateBelowInput(byte[] bgra, int width, int stride, int height, int bottom, out BottomBar bar)
    {
        bar = default;
        var floor = Math.Max(0, bottom - Math.Max(80, height / 5));
        var runTop = -1;
        var runBottom = -1;
        var bestTop = -1;
        var bestBottom = -1;
        var bestLen = 0;
        for (var y = bottom; y >= floor; y--)
        {
            if (LeftMedian(bgra, width, stride, y) >= 200)
            {
                if (runBottom < 0) runBottom = y;
                runTop = y;
                continue;
            }

            ConsiderRun();
        }

        ConsiderRun();
        if (bestLen < 10) return false;

        var plateTop = bestBottom + 1;
        var plateHeight = bottom - plateTop + 1;
        var minHeight = Math.Max(16, height / 50);
        var maxHeight = Math.Max(minHeight + 8, height / 5);
        if (plateHeight < minHeight || plateHeight > maxHeight) return false;

        bar = new BottomBar(plateTop, bottom + 1, true, bestTop);
        return true;

        void ConsiderRun()
        {
            if (runBottom < 0 || runTop < 0) return;
            var len = runBottom - runTop + 1;
            if (len > bestLen)
            {
                bestLen = len;
                bestTop = runTop;
                bestBottom = runBottom;
            }

            runTop = -1;
            runBottom = -1;
        }
    }

    private static int LeftMedian(byte[] bgra, int width, int stride, int y)
    {
        Span<int> lums = stackalloc int[48];
        var count = 0;
        var stop = Math.Max(8, (int)(width * 0.45));
        var step = Math.Max(1, stop / 40);
        var row = y * stride;
        for (var x = 0; x < stop && count < lums.Length; x += step)
        {
            var i = row + x * 4;
            lums[count++] = (bgra[i + 2] + bgra[i + 1] + bgra[i]) / 3;
        }

        if (count == 0) return 0;
        lums[..count].Sort();
        return lums[count / 2];
    }

    private static bool RowBlack(byte[] bgra, int width, int stride, int y)
    {
        var dark = 0;
        var samples = 0;
        var step = Math.Max(1, width / 40);
        var row = y * stride;
        for (var x = 0; x < width; x += step)
        {
            var i = row + x * 4;
            samples++;
            if (bgra[i + 2] < 12 && bgra[i + 1] < 12 && bgra[i] < 12) dark++;
        }

        return samples > 0 && dark * 4 >= samples * 3;
    }

    private static bool ChromeRow(byte[] bgra, int width, int stride, int y, out int median)
    {
        Span<int> lums = stackalloc int[96];
        var count = 0;
        var step = Math.Max(1, width / 80);
        var row = y * stride;
        for (var x = 0; x < width && count < lums.Length; x += step)
        {
            var i = row + x * 4;
            var r = bgra[i + 2];
            var g = bgra[i + 1];
            var b = bgra[i];
            if (r < 12 && g < 12 && b < 12) continue;
            lums[count++] = (r + g + b) / 3;
        }

        if (count < 8)
        {
            median = 0;
            return false;
        }

        lums[..count].Sort();
        median = lums[count / 2];
        if (median < 28 || median > 210)
        {
            return false;
        }

        var mid = 0;
        for (var i = 0; i < count; i++) mid += Math.Abs(lums[i] - median);
        var mad = mid / count;
        return mad <= 18;
    }
}
