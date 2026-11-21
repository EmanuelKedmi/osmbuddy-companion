namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Reads the minimap title band. The panel is tried first. The ink mask
/// runs only when that read is not a catalog map from its own line, so a gap
/// the mask opens in a thin letter cannot replace a name the panel already
/// got right. A panel twice the size is the last try for small text.
/// </summary>
public static class MapTitleRead
{
    public static IReadOnlyList<string> Lines(byte[] bgra, int width, int height)
    {
        if (!WindowsOcr.IsAvailable || width <= 0 || height <= 0 || bgra.Length != width * height * 4)
        {
            return [];
        }

        var panel = WindowsOcr.ReadLines(bgra, width, height);
        if (Full(panel)) return panel;

        var prepared = MapTitleImage.Prepare(bgra, width, height, out var outWidth, out var outHeight);
        IReadOnlyList<string> ink = [];
        if (outWidth > 1 && outHeight > 1 && prepared.Length == outWidth * outHeight * 4)
        {
            ink = WindowsOcr.ReadLines(prepared, outWidth, outHeight);
            if (Full(ink)) return ink;
        }

        var large = WindowsOcr.ReadLines(GameFrame.PointScale(bgra, width, height, width * 2, height * 2), width * 2, height * 2);
        if (Full(large)) return large;

        // OCR can return the town line and skip the map line under it.
        // The lower half alone, enlarged, is read as the map line.
        var lower = LowerHalf(bgra, width, height, out var halfHeight);
        if (halfHeight > 1)
        {
            var mapLine = WindowsOcr.ReadLines(GameFrame.PointScale(lower, width, halfHeight, width * 2, halfHeight * 2), width * 2, halfHeight * 2);
            var combined = panel.Take(1).Concat(mapLine).ToList();
            if (Full(combined)) return combined;
        }

        return MapTitle.Prefer(panel, ink);
    }

    private static byte[] LowerHalf(byte[] bgra, int width, int height, out int halfHeight)
    {
        var top = height * 2 / 5;
        halfHeight = height - top;
        var stride = width * 4;
        var lower = new byte[halfHeight * stride];
        Buffer.BlockCopy(bgra, top * stride, lower, 0, lower.Length);
        return lower;
    }

    private static bool Full(IReadOnlyList<string> lines) =>
        MapTitle.Choose(lines) is { FromCatalog: true, StreetOnly: false };
}
