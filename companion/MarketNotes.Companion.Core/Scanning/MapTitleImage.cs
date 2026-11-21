namespace MarketNotes.Companion.Scanning;

/// <summary>
/// The minimap paints the map name in light letters on top of the map.
/// Windows OCR folds that drawing into the letters, so "Forest II" comes
/// back as "ForeWll". Keep the light ink, drop the map, and enlarge the
/// bitmap font before it is read.
/// </summary>
public static class MapTitleImage
{
    public const int Scale = 3;

    public static bool IsInk(byte r, byte g, byte b)
    {
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        if (max < 220 || min < 140) return false;
        if (g > r + 22 && g > b + 22) return false;
        return true;
    }

    public static byte[] Prepare(byte[] bgra, int width, int height, out int outWidth, out int outHeight)
    {
        outWidth = 1;
        outHeight = 1;
        if (bgra.Length == 0 || width <= 0 || height <= 0 || bgra.Length != width * height * 4)
        {
            return [0, 0, 0, 255];
        }

        var ink = new byte[bgra.Length];
        var top = height;
        var left = width;
        var bottom = -1;
        var right = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (!IsInk(bgra[i + 2], bgra[i + 1], bgra[i])) continue;
                ink[i] = 255;
                ink[i + 1] = 255;
                ink[i + 2] = 255;
                ink[i + 3] = 255;
                if (y < top) top = y;
                if (x < left) left = x;
                if (y > bottom) bottom = y;
                if (x > right) right = x;
            }
        }

        if (bottom < top || right < left) return [0, 0, 0, 255];

        const int pad = 2;
        top = Math.Max(0, top - pad);
        left = Math.Max(0, left - pad);
        bottom = Math.Min(height - 1, bottom + pad);
        right = Math.Min(width - 1, right + pad);
        var cropWidth = right - left + 1;
        var cropHeight = bottom - top + 1;
        var cropped = new byte[cropWidth * cropHeight * 4];
        for (var y = 0; y < cropHeight; y++)
        {
            Buffer.BlockCopy(ink, ((top + y) * width + left) * 4, cropped, y * cropWidth * 4, cropWidth * 4);
        }

        return Enlarge(cropped, cropWidth, cropHeight, Scale, out outWidth, out outHeight);
    }

    public static byte[] Enlarge(byte[] bgra, int width, int height, int factor, out int outWidth, out int outHeight)
    {
        factor = Math.Max(1, factor);
        outWidth = width * factor;
        outHeight = height * factor;
        var scaled = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = (y * width + x) * 4;
                for (var dy = 0; dy < factor; dy++)
                {
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var dest = ((y * factor + dy) * outWidth + (x * factor + dx)) * 4;
                        scaled[dest] = bgra[source];
                        scaled[dest + 1] = bgra[source + 1];
                        scaled[dest + 2] = bgra[source + 2];
                        scaled[dest + 3] = bgra[source + 3];
                    }
                }
            }
        }

        return scaled;
    }
}
