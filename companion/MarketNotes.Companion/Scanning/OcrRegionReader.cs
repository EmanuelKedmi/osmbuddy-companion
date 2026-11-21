namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Shared OCR seam for cropped regions. <see cref="EngineInstalled"/> stays
/// false until a real engine is compiled in. Callers must not invent text
/// when this returns null.
/// </summary>
public static class OcrRegionReader
{
    public static bool EngineInstalled() => OcrStoreScanner.EngineInstalled();

    public static IReadOnlyList<string> ReadLines(byte[] bgra, int width, int height)
    {
        if (!EngineInstalled() || bgra.Length == 0) return [];
        return WindowsOcr.ReadLines(bgra, width, height);
    }
}
