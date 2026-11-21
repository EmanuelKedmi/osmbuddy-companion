namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Seam for a later OCR engine. This project does not reference Tesseract
/// or Windows.Media.Ocr, and <see cref="EngineInstalled"/> stays false so
/// <see cref="ScannerFactory"/> keeps the sample scanner.
///
/// To plug an engine in:
/// 1. Add Tesseract, or the WinRT projection for Windows.Media.Ocr.
/// 2. Crop <see cref="Capture.CapturedFrame.Bgra"/> to the shop grid.
/// 3. Recognize each cell into item, quantity, and priceEach.
/// 4. Return a <see cref="StoreScan"/> with <see cref="StoreScan.IsSampleData"/> false
///    and do not prefix item names with "[Sample]".
/// 5. Return true from <see cref="EngineInstalled"/> only when that engine can run.
/// </summary>
public sealed class OcrStoreScanner : IStoreScanner
{
    public string Name => "OCR";
    public bool IsLiveOcr => true;

    /// <summary>
    /// False until a real engine is compiled in. Do not flip this to hide sample data.
    /// </summary>
    public static bool EngineInstalled() => WindowsOcr.IsAvailable;

    public StoreScan Scan(StoreScanRequest request)
    {
        throw new NotSupportedException(
            "Live OCR is not implemented. Leave EngineInstalled false so the sample scanner runs.");
    }
}

public static class ScannerFactory
{
    public static IStoreScanner Create()
    {
        if (OcrStoreScanner.EngineInstalled())
        {
            return new OcrStoreScanner();
        }

        return new SampleStoreScanner();
    }
}
