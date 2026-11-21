namespace MarketNotes.Companion.Farm;

/// <summary>
/// Seam for a later OCR pass over <see cref="DropRegion"/>. This project does
/// not reference Tesseract or Windows.Media.Ocr. <see cref="EngineInstalled"/>
/// stays false so a changed chat band is never reported as item names.
/// </summary>
public static class OcrDropNoticeReader
{
    /// <summary>
    /// False until a real engine is compiled in. Do not flip this to hide a fixture.
    /// </summary>
    public static bool EngineInstalled() => Scanning.WindowsOcr.IsAvailable;
}
