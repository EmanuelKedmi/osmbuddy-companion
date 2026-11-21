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
}
