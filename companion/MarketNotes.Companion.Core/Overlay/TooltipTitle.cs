using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// The title band redrawn as black letters on white for OCR. Titles are
/// white, purple, orange, or blue on the tooltip navy; all of them are far
/// brighter than the navy, so brightness alone separates them.
/// </summary>
public static class TooltipTitle
{
    private const int Pad = 8;

    public readonly record struct InkImage(byte[] Bgra, int Width, int Height);

    public static InkImage Ink(byte[] bgra, int width, int height, int stride, PixelRect title, int scale)
    {
        scale = Math.Clamp(scale, 1, 4);
        var x0 = Math.Clamp(title.X, 0, width);
        var y0 = Math.Clamp(title.Y, 0, height);
        var x1 = Math.Clamp(title.Right, x0, width);
        var y1 = Math.Clamp(title.Bottom, y0, height);
        var outWidth = (x1 - x0) * scale + Pad * 2;
        var outHeight = (y1 - y0) * scale + Pad * 2;
        var ink = new byte[outWidth * outHeight * 4];
        Array.Fill(ink, (byte)255);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = y * stride + x * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                if (r + g + b < 390 && Math.Max(r, Math.Max(g, b)) < 190) continue;
                for (var dy = 0; dy < scale; dy++)
                {
                    var row = (Pad + (y - y0) * scale + dy) * outWidth;
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var at = (row + Pad + (x - x0) * scale + dx) * 4;
                        ink[at] = 0;
                        ink[at + 1] = 0;
                        ink[at + 2] = 0;
                    }
                }
            }
        }

        return new InkImage(ink, outWidth, outHeight);
    }

    /// <summary>Hash of the band, so OCR runs only when the title changes.</summary>
    public static int Hash(byte[] bgra, int width, int height, int stride, PixelRect title)
    {
        unchecked
        {
            var hash = (int)2166136261;
            var y0 = Math.Max(0, title.Y);
            var x0 = Math.Max(0, title.X);
            for (var y = y0; y < Math.Min(height, title.Bottom); y += 2)
            {
                for (var x = x0; x < Math.Min(width, title.Right); x += 2)
                {
                    var i = y * stride + x * 4;
                    var lit = bgra[i] + bgra[i + 1] + bgra[i + 2] >= 390 ? 1 : 0;
                    hash = (hash ^ ((x - x0) * 31 + (y - y0) * 17 + lit)) * 16777619;
                }
            }

            return hash;
        }
    }
}
