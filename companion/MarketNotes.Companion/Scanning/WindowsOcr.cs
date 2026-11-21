using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Windows.Media.Ocr, the reader that ships with Windows 10 and 11.
/// It is on only when an English OCR language pack is installed.
/// </summary>
public static class WindowsOcr
{
    public const string InstallHint =
        "Windows OCR needs the English language pack. Open Settings, go to Time & language, Language & region, English (United States), Language options, and download Optical character recognition. Then start the companion again.";

    private static readonly object Gate = new();
    private static OcrEngine? _engine;
    private static bool _probed;
    private static string _status = "Windows OCR has not been checked yet.";

    public static bool IsAvailable
    {
        get
        {
            Ensure();
            return _engine != null;
        }
    }

    public static string Status
    {
        get
        {
            Ensure();
            return _status;
        }
    }

    public readonly record struct OcrLine(string Text, string Words);

    public static IReadOnlyList<string> ReadLines(byte[] bgra, int width, int height)
    {
        return ReadDetail(bgra, width, height).Select(line => line.Text).ToList();
    }

    public static IReadOnlyList<OcrLine> ReadDetail(byte[] bgra, int width, int height)
    {
        if (!IsAvailable || bgra.Length == 0 || width <= 0 || height <= 0) return [];
        if (bgra.Length != width * height * 4) return [];
        var engine = _engine;
        if (engine == null) return [];

        try
        {
            return Task.Run(() => Recognize(engine, bgra, width, height)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return [new OcrLine($"(OCR failed: {ex.GetType().Name})", "")];
        }
    }

    private static void Ensure()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            try
            {
                _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                    ?? OcrEngine.TryCreateFromLanguage(new Language("en"))
                    ?? OcrEngine.TryCreateFromUserProfileLanguages();
                _status = _engine == null
                    ? InstallHint
                    : "Windows OCR is on (English).";
            }
            catch (Exception ex)
            {
                _engine = null;
                _status = $"Windows OCR could not start ({ex.GetType().Name}). {InstallHint}";
            }

            _probed = true;
        }
    }

    private static async Task<IReadOnlyList<OcrLine>> Recognize(OcrEngine engine, byte[] bgra, int width, int height)
    {
        var opaque = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, opaque, 0, bgra.Length);
        for (var i = 3; i < opaque.Length; i += 4) opaque[i] = 255;

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            opaque.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap);
        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0) continue;
            var words = string.Join(" · ", line.Words.Select(word => word.Text.Trim()).Where(word => word.Length > 0));
            lines.Add(new OcrLine(text, words));
        }

        return lines;
    }
}
