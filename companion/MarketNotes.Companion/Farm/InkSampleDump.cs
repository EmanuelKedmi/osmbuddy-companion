using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using MarketNotes.Companion.Modes;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// Raw battle and loot crops, plus the OCR text that frame produced.
/// Debug only. A ring of 30 files per kind; dev builds keep 400 loot crops for
/// the OCR bench. The website token is never written.
/// </summary>
public static class InkSampleDump
{
    public const int MaxEach = 30;
    public const int MaxLootDev = 400;

    public static string Root()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "MarketNotes.Companion", "samples");
    }

    public static void Save(string kind, byte[] bgra, int width, int height, string ocr)
    {
        if (!DebugLog.Enabled) return;
        if (kind is not ("battle" or "loot" or "shop" or "shop-title" or "shop-rows" or "sheet")) return;
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4) return;
        try
        {
            var directory = Path.Combine(Root(), kind);
            Directory.CreateDirectory(directory);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var png = Path.Combine(directory, stamp + ".png");
            WritePng(png, bgra, width, height);
            File.WriteAllText(Path.Combine(directory, stamp + ".txt"), ocr ?? "");
            Trim(directory, kind == "loot" && DebugLog.DevBuild ? MaxLootDev : MaxEach);
        }
        catch
        {
            // A blocked samples folder does not change what OCR reads.
        }
    }

    private static void Trim(string directory, int keep)
    {
        var files = new DirectoryInfo(directory).GetFiles("*.png");
        if (files.Length <= keep) return;
        foreach (var file in files.OrderBy(file => file.Name, StringComparer.Ordinal).Take(files.Length - keep))
        {
            file.Delete();
            var note = Path.ChangeExtension(file.FullName, ".txt");
            if (File.Exists(note)) File.Delete(note);
        }
    }

    private static void WritePng(string path, byte[] bgra, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(bgra, y * row, data.Scan0 + y * data.Stride, row);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}
