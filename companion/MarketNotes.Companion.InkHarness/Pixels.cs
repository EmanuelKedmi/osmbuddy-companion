using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MarketNotes.Companion.InkHarness;

static class Pixels
{
    public static (byte[] Bgra, int Width, int Height) Load(string path)
    {
        using var bitmap = new Bitmap(path);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = new byte[width * height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return (pixels, width, height);
    }

    public static void Save(string path, byte[] bgra, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    public static byte[] Crop(byte[] bgra, int width, int x, int y, int cropWidth, int cropHeight)
    {
        var dest = new byte[cropWidth * cropHeight * 4];
        for (var row = 0; row < cropHeight; row++)
        {
            Buffer.BlockCopy(bgra, ((y + row) * width + x) * 4, dest, row * cropWidth * 4, cropWidth * 4);
        }

        return dest;
    }

    public static byte[] Scale(byte[] bgra, int width, int height, int factor)
    {
        if (factor <= 1) return bgra;
        var dw = width * factor;
        var dh = height * factor;
        var dest = new byte[dw * dh * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = (y * width + x) * 4;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = ((y * factor) + dy) * dw;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var to = (row + (x * factor) + dx) * 4;
                        dest[to] = bgra[from];
                        dest[to + 1] = bgra[from + 1];
                        dest[to + 2] = bgra[from + 2];
                        dest[to + 3] = 255;
                    }
                }
            }
        }

        return dest;
    }

    public static byte[] PadScale(byte[] bgra, int width, int height, int pad, int scale, byte fill)
    {
        var outW = (width + pad * 2) * scale;
        var outH = (height + pad * 2) * scale;
        var dest = new byte[outW * outH * 4];
        for (var i = 0; i < dest.Length; i += 4)
        {
            dest[i] = fill;
            dest[i + 1] = fill;
            dest[i + 2] = fill;
            dest[i + 3] = 255;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = (y * width + x) * 4;
                for (var dy = 0; dy < scale; dy++)
                {
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var to = (((y + pad) * scale + dy) * outW + ((x + pad) * scale + dx)) * 4;
                        dest[to] = bgra[from];
                        dest[to + 1] = bgra[from + 1];
                        dest[to + 2] = bgra[from + 2];
                        dest[to + 3] = 255;
                    }
                }
            }
        }

        return dest;
    }

    /// <summary>White loot ink becomes black on white. Black battle ink stays black on white.</summary>
    public static byte[] BlackOnWhite(byte[] mask, int width, int height, bool whiteInk)
    {
        var dest = new byte[mask.Length];
        for (var i = 0; i < mask.Length; i += 4)
        {
            var ink = whiteInk ? mask[i] > 127 : mask[i] < 128;
            var v = (byte)(ink ? 0 : 255);
            dest[i] = v;
            dest[i + 1] = v;
            dest[i + 2] = v;
            dest[i + 3] = 255;
        }

        return dest;
    }

    public static void SaveTrio(string path, byte[] raw, byte[] left, byte[] right, int width, int height)
    {
        const int gap = 8;
        const int scale = 2;
        var panelW = width * scale;
        var panelH = height * scale;
        var outW = panelW * 3 + gap * 2;
        var outH = panelH;
        var dest = new byte[outW * outH * 4];
        for (var i = 0; i < dest.Length; i += 4)
        {
            dest[i] = 40;
            dest[i + 1] = 40;
            dest[i + 2] = 40;
            dest[i + 3] = 255;
        }

        Blit(raw, 0);
        Blit(left, panelW + gap);
        Blit(right, (panelW + gap) * 2);
        Save(path, dest, outW, outH);

        void Blit(byte[] src, int destX)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var from = (y * width + x) * 4;
                    for (var dy = 0; dy < scale; dy++)
                    {
                        for (var dx = 0; dx < scale; dx++)
                        {
                            var to = ((y * scale + dy) * outW + destX + x * scale + dx) * 4;
                            dest[to] = src[from];
                            dest[to + 1] = src[from + 1];
                            dest[to + 2] = src[from + 2];
                            dest[to + 3] = 255;
                        }
                    }
                }
            }
        }
    }
}
