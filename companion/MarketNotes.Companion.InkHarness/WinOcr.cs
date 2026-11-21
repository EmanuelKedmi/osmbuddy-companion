using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MarketNotes.Companion.InkHarness;

static class WinOcr
{
    private static readonly OcrEngine? Engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
        ?? OcrEngine.TryCreateFromLanguage(new Language("en"))
        ?? OcrEngine.TryCreateFromUserProfileLanguages();

    public static bool Available => Engine != null;

    public static IReadOnlyList<string> Read(byte[] bgra, int width, int height)
    {
        if (Engine == null || width <= 0 || height <= 0 || bgra.Length != width * height * 4) return [];
        var opaque = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, opaque, 0, bgra.Length);
        for (var i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            opaque.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Ignore);
        var result = Engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        var lines = new List<string>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length > 0) lines.Add(text);
        }

        return lines;
    }
}
