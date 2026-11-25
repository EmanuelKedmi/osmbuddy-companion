namespace MarketNotes.Companion.Scanning;

/// <summary>
/// White pickup lines sit in the bottom-right, above the skill bar.
/// The lower-left of the client is the chat.
/// Lines are right-aligned to the client edge. The top line can start
/// near 0.68, so the band includes that margin and the last pixels.
/// </summary>
public static class LootRegion
{
    public const double Left = 0.62;
    public const double Top = 0.68;
    public const double Right = 1.0;
    public const double Bottom = 0.88;

    public static PixelRect Bounds(int width, int height)
    {
        var x = Math.Clamp((int)(width * Left), 0, Math.Max(0, width - 1));
        var y = Math.Clamp((int)(height * Top), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling(width * Right), x + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(height * Bottom), y + 1, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }

    /// <summary>
    /// The same band, sitting just above the measured status bar. On the
    /// 1024×588 client the bar is the bottom tenth and this matches
    /// <see cref="Bounds"/>. A shorter bar pulls the band down with it.
    /// </summary>
    public static PixelRect Place(int width, int height, BottomBar bar)
    {
        if (!bar.Measured) return Bounds(width, height);
        var edge = bar.ChromeTop;
        // The input row already separates the notices from the plate.
        // A gap the size of the plate would sit above the lowest line.
        var gap = edge < bar.Top ? 2 : Math.Max(2, (int)(bar.Height * 0.20));
        var band = Math.Max(bar.Height * 2, (int)(height * 0.20));
        var bottom = edge - gap;
        var top = bottom - band;
        if (top < 0) top = 0;
        if (bottom <= top) bottom = Math.Min(height, top + 1);
        var x = Math.Clamp((int)(width * Left), 0, Math.Max(0, width - 1));
        var right = Math.Clamp(width, x + 1, width);
        return new PixelRect(x, top, right - x, bottom - top);
    }

    public static PixelRect ForFrame(byte[] bgra, int width, int height, int stride) =>
        Place(width, height, BottomBarFinder.Measure(bgra, width, height, stride));
}
