namespace MarketNotes.Companion.Scanning;

/// <summary>
/// The character name on this client is the lower line of the status plate
/// in the bottom-left of the window (job above, name below, HP to the right).
/// On the 1024×588 training frame that line is ExtraCold, around
/// x 90–210 and y 566–584. The floating name over the sprite moves, and a
/// loot crop that includes it was read as a drop. This box stays left of
/// the loot band and below it.
/// </summary>
public static class CharacterNameRegion
{
    public const double Left = 0.07;
    public const double Top = 0.935;
    public const double Right = 0.22;
    public const double Bottom = 0.992;

    public static PixelRect Bounds(int width, int height)
    {
        var x = Math.Clamp((int)(width * Left), 0, Math.Max(0, width - 1));
        var y = Math.Clamp((int)(height * Top), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling(width * Right), x + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(height * Bottom), y + 1, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }
}
