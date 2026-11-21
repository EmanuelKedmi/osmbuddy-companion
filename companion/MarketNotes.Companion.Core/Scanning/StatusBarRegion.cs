namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Bottom status plate: LV, job, name, HP/MP/EXP bars. Wider than
/// <see cref="CharacterNameRegion"/> so level and max HP stay readable
/// while the Stat window is closed.
/// </summary>
public static class StatusBarRegion
{
    public const double Left = 0.00;
    public const double Top = 0.90;
    public const double Right = 0.55;
    public const double Bottom = 1.00;

    /// <summary>Just the LV badge; OCR often drops it from the wide plate crop.</summary>
    public const double LevelLeft = 0.00;
    public const double LevelTop = 0.92;
    public const double LevelRight = 0.08;
    public const double LevelBottom = 0.99;

    public static PixelRect Bounds(int width, int height) => Box(width, height, Left, Top, Right, Bottom);

    public static PixelRect LevelBounds(int width, int height) =>
        Box(width, height, LevelLeft, LevelTop, LevelRight, LevelBottom);

    private static PixelRect Box(int width, int height, double left, double top, double right, double bottom)
    {
        var x = Math.Clamp((int)(width * left), 0, Math.Max(0, width - 1));
        var y = Math.Clamp((int)(height * top), 0, Math.Max(0, height - 1));
        var edge = Math.Clamp((int)Math.Ceiling(width * right), x + 1, width);
        var floor = Math.Clamp((int)Math.Ceiling(height * bottom), y + 1, height);
        return new PixelRect(x, y, edge - x, floor - y);
    }
}
