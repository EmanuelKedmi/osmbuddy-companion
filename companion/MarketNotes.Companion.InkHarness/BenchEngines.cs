using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RapidOcrNet;
using SkiaSharp;
using Tesseract;

namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// PaddleOCR through RapidOcrNet. CPU only. Lines top to bottom.
/// "latin" is the v5 set bundled with the package. "en" swaps in the English v5
/// recognizer and "v6" the v6 small pair, both from ocr-models\paddle. Their
/// dictionaries live in the ONNX metadata and are written out on first use.
/// </summary>
static class Paddle
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RapidOcr> Loaded = new(StringComparer.Ordinal);
    private static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "models", "v5");
    private static readonly string Extra = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarketNotes.Companion", "ocr-models", "paddle");

    public static List<string> Read(byte[] bgra, int width, int height, int scale, string model = "latin")
    {
        return Detect(bgra, width, height, scale, model).TextBlocks
            .OrderBy(block => block.BoxPoints.Average(point => (double)point.Y))
            .Select(block => block.Text.Trim())
            .Where(text => text.Length > 0)
            .ToList();
    }

    /// <summary>Boxes that share a row joined left to right, as the companion reads the loot corner.</summary>
    public static List<string> ReadRows(byte[] bgra, int width, int height, int scale, string model = "latin")
    {
        var blocks = Detect(bgra, width, height, scale, model).TextBlocks;
        return MarketNotes.Companion.Farm.NoticeRows.Join(blocks.Select(block => new MarketNotes.Companion.Farm.PlacedBox(
                block.Text.Trim(),
                block.BoxPoints.Min(point => (double)point.X) / scale,
                block.BoxPoints.Min(point => (double)point.Y) / scale,
                block.BoxPoints.Max(point => (double)point.X) / scale,
                block.BoxPoints.Max(point => (double)point.Y) / scale)), splitGaps: false)
            .Select(line => line.Text)
            .ToList();
    }

    private static OcrResult Detect(byte[] bgra, int width, int height, int scale, string model)
    {
        if (!Loaded.TryGetValue(model, out var ocr))
        {
            ocr = Start(model);
            Loaded[model] = ocr;
        }

        using var source = Bitmap(bgra, width, height);
        using var input = scale <= 1
            ? source.Copy()
            : source.Resize(new SKImageInfo(width * scale, height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
        var options = model.StartsWith("v6", StringComparison.Ordinal)
            ? RapidOcrOptions.PPOCRv6 with { DoAngle = false, LimitSideLen = Math.Max(736, Math.Min(input.Width, input.Height)) }
            : RapidOcrOptions.Default with { DoAngle = false, ImgResize = Math.Max(1024, Math.Max(input.Width, input.Height)) };
        return ocr.Detect(input, options);
    }

    private static RapidOcr Start(string key)
    {
        var copy = key.IndexOf('#');
        if (copy >= 0) key = key[..copy];
        var at = key.IndexOf('@');
        var model = at < 0 ? key : key[..at];
        var threads = at < 0 ? 0 : int.Parse(key[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var det = Path.Combine(Bundled, "ch_PP-OCRv5_mobile_det.onnx");
        var cls = Path.Combine(Bundled, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        var rec = Path.Combine(Bundled, "latin_PP-OCRv5_rec_mobile_infer.onnx");
        var keys = Path.Combine(Bundled, "ppocrv5_latin_dict.txt");
        if (model == "en")
        {
            rec = Path.Combine(Extra, "en_PP-OCRv5_rec_mobile.onnx");
            keys = Keys(rec);
        }
        else if (model == "v6")
        {
            det = Path.Combine(Extra, "PP-OCRv6_det_small.onnx");
            rec = Path.Combine(Extra, "PP-OCRv6_rec_small.onnx");
            keys = Keys(rec);
        }

        var ocr = new RapidOcr();
        var session = RapidOcr.GetDefaultSessionOptions(threads);
        if (model.EndsWith("-noarena", StringComparison.Ordinal)) session.EnableCpuMemArena = false;
        ocr.InitModels(det, cls, rec, keys, session);
        return ocr;
    }

    private static string Keys(string onnx)
    {
        var path = Path.ChangeExtension(onnx, ".dict.txt");
        if (File.Exists(path)) return path;
        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(onnx);
        var chars = session.ModelMetadata.CustomMetadataMap["character"];
        File.WriteAllText(path, chars.Replace("\r\n", "\n"));
        return path;
    }

    private static SKBitmap Bitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var opaque = (byte[])bgra.Clone();
        for (var i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
        Marshal.Copy(opaque, 0, bitmap.GetPixels(), opaque.Length);
        return bitmap;
    }
}

/// <summary>Tesseract 5 LSTM with tessdata_best English from %LOCALAPPDATA%\MarketNotes.Companion\ocr-models\tessdata.</summary>
static class Tess
{
    private static TesseractEngine? _engine;

    public static List<string> Read(byte[] bgra, int width, int height)
    {
        _engine ??= new TesseractEngine(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarketNotes.Companion", "ocr-models", "tessdata"),
            "eng",
            EngineMode.LstmOnly);
        using var pix = Pix.LoadFromMemory(Png(bgra, width, height));
        using var page = _engine.Process(pix, PageSegMode.SingleBlock);
        return page.GetText()
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static byte[] Png(byte[] bgra, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++) Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }
}
