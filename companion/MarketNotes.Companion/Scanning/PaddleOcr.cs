using System.IO;
using System.Runtime.InteropServices;
using MarketNotes.Companion.Farm;
using RapidOcrNet;
using SkiaSharp;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// PaddleOCR v5 latin (RapidOcrNet, ONNX, CPU) for the loot corner.
/// It reads the untouched crop at 2x: fading gray notices survive, where the
/// white-ink mask in front of Windows OCR erased them. One shared model set;
/// several readers may call at once, each read on its own single thread (the
/// ONNX default spins every core and costs about 2.4 s of CPU per frame).
/// The memory arena is off: eight readers hold about 440 MB instead of 1.3 GB.
/// </summary>
public static class PaddleOcr
{
    public const int Threads = 1;
    public const int Scale = 2;

    private static readonly string[] Files =
    [
        "ch_PP-OCRv5_mobile_det.onnx",
        "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        "latin_PP-OCRv5_rec_mobile_infer.onnx",
        "ppocrv5_latin_dict.txt",
    ];

    private static readonly object Gate = new();
    private static RapidOcr? _ocr;
    private static bool _probed;
    private static string _status = "PaddleOCR has not been started yet.";

    public static bool IsAvailable
    {
        get
        {
            Ensure();
            return _ocr != null;
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

    public static IReadOnlyList<PlacedLine> Read(byte[] bgra, int width, int height, bool splitGaps = true)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4) return [];
        Ensure();
        var ocr = _ocr;
        if (ocr == null) return [];
        using var source = Bitmap(bgra, width, height);
        using var input = source.Resize(
            new SKImageInfo(width * Scale, height * Scale, SKColorType.Bgra8888, SKAlphaType.Opaque),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        var options = RapidOcrOptions.Default with
        {
            DoAngle = false,
            ImgResize = Math.Max(1024, Math.Max(input.Width, input.Height)),
        };
        var result = ocr.Detect(input, options);
        return NoticeRows.Join(result.TextBlocks.Select(block => new PlacedBox(
            block.Text.Trim(),
            block.BoxPoints.Min(point => (double)point.X) / Scale,
            block.BoxPoints.Min(point => (double)point.Y) / Scale,
            block.BoxPoints.Max(point => (double)point.X) / Scale,
            block.BoxPoints.Max(point => (double)point.Y) / Scale)), splitGaps);
    }

    private static void Ensure()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            try
            {
                var dir = Unpack();
                var ocr = new RapidOcr();
                var session = RapidOcr.GetDefaultSessionOptions(Threads);
                session.EnableCpuMemArena = false;
                ocr.InitModels(
                    Path.Combine(dir, Files[0]),
                    Path.Combine(dir, Files[1]),
                    Path.Combine(dir, Files[2]),
                    Path.Combine(dir, Files[3]),
                    session);
                _ocr = ocr;
                _status = "PaddleOCR is on for the loot corner.";
            }
            catch (Exception ex)
            {
                _ocr = null;
                _status = $"PaddleOCR could not start ({ex.GetType().Name}: {ex.Message}). The loot corner falls back to Windows OCR.";
            }

            _probed = true;
        }
    }

    private static string Unpack()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        var dir = Path.Combine(root, "MarketNotes.Companion", "ocr-models", "paddle-v5");
        Directory.CreateDirectory(dir);
        var assembly = typeof(PaddleOcr).Assembly;
        foreach (var name in Files)
        {
            using var resource = assembly.GetManifestResourceStream("paddle-v5/" + name)
                ?? throw new FileNotFoundException("The companion was built without the PaddleOCR model " + name + ".");
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && new FileInfo(path).Length == resource.Length) continue;
            var temp = path + ".part";
            using (var file = File.Create(temp))
            {
                resource.CopyTo(file);
            }

            File.Move(temp, path, overwrite: true);
        }

        return dir;
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
