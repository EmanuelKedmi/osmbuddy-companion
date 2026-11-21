using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// Overwrites the latest isolated crops so they can be opened full size.
/// Unchanged crops are not written. The website token is never written here.
/// </summary>
public static class IsolationPreviewFiles
{
    public static string DirectoryPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "MarketNotes.Companion", "preview");
    }

    public static void WriteBattle(byte[] bgra, int width, int height) => Write("battle.png", bgra, width, height, opaque: false);

    public static void WriteLoot(byte[] bgra, int width, int height) => Write("loot.png", bgra, width, height, opaque: true);

    private static void Write(string name, byte[] bgra, int width, int height, bool opaque)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4) return;
        try
        {
            var pixels = opaque ? Opaque(bgra) : bgra;
            var directory = DirectoryPath();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var bounds = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = width * 4;
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var temp = path + ".writing";
            File.WriteAllBytes(temp, stream.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A locked preview file does not change what OCR reads.
        }
    }

    /// <summary>
    /// Loot isolation leaves the cleared pixels transparent. Windows OCR forces
    /// that alpha opaque before it reads, and a PNG viewer needs the same or
    /// the white glyphs disappear. RGB is unchanged.
    /// </summary>
    private static byte[] Opaque(byte[] bgra)
    {
        var copy = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, copy, 0, bgra.Length);
        for (var i = 3; i < copy.Length; i += 4) copy[i] = 255;
        return copy;
    }
}
