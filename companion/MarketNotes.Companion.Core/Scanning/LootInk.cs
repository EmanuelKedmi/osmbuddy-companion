namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Pickup lines are near-white. The map behind them is bright green and brown,
/// and Windows OCR reads that texture as part of the letters. Keep the ink and
/// paint everything else black before the loot corner is read.
/// </summary>
public static class LootInk
{
    public static bool IsInk(byte r, byte g, byte b)
    {
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        if (min < 165) return false;
        if (max - min > 48) return false;
        // Bright leaves are green-heavy. The glyph stays balanced.
        if (g > r + 22 && g > b + 22) return false;
        return true;
    }

    public static byte[] Isolate(byte[] bgra, int width, int height)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0 || bgra.Length != width * height * 4)
        {
            return bgra;
        }

        var ink = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (!IsInk(bgra[i + 2], bgra[i + 1], bgra[i])) continue;
            ink[i] = 255;
            ink[i + 1] = 255;
            ink[i + 2] = 255;
            ink[i + 3] = 255;
        }

        return ink;
    }

    /// <summary>
    /// White glyphs on a dark map, and gray glyphs on a pale sky.
    /// A flat bright sky passes <see cref="IsInk"/> and swallows the letters,
    /// so a pixel has to stand out from its neighborhood. Blobs taller than
    /// one notice line are dropped. Output is white ink on black.
    /// </summary>
    public static byte[] IsolateNotice(byte[] bgra, int width, int height)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0 || bgra.Length != width * height * 4)
        {
            return bgra;
        }

        var mean = LocalMean(bgra, width, height);
        var dest = new byte[bgra.Length];
        for (var i = 0; i < width * height; i++)
        {
            var r = bgra[i * 4 + 2];
            var g = bgra[i * 4 + 1];
            var b = bgra[i * 4];
            var min = Math.Min(r, Math.Min(g, b));
            var max = Math.Max(r, Math.Max(g, b));
            var lum = (r + g + b) / 3;
            var green = g > r + 22 && g > b + 22;
            var gold = r >= 180 && g >= 90 && g <= 210 && b <= 120 && r > g + 25;
            var neutral = max - min <= 40;
            var whiteOnDark = min >= 165 && max - min <= 48 && !green && !gold && lum >= mean[i] + 28;
            var grayOnSky = neutral && !green && !gold && mean[i] >= 185 && lum + 28 <= mean[i] && lum >= 155 && lum <= 215;
            if (!whiteOnDark && !grayOnSky) continue;
            dest[i * 4] = 255;
            dest[i * 4 + 1] = 255;
            dest[i * 4 + 2] = 255;
            dest[i * 4 + 3] = 255;
        }

        return KeepSmall(dest, width, height, 22, 280);
    }

    /// <summary>Horizontal runs of white ink. A notice line is a short run; a filled sky is not.</summary>
    public static List<(int Top, int Bottom)> TextRows(byte[] whiteInk, int width, int height)
    {
        var rows = new List<(int Top, int Bottom)>();
        if (width <= 0 || height <= 0 || whiteInk.Length != width * height * 4) return rows;
        var top = -1;
        for (var y = 0; y <= height; y++)
        {
            var count = 0;
            if (y < height)
            {
                var row = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    if (whiteInk[row + x * 4] != 0) count++;
                }
            }

            var on = count >= 4;
            if (on)
            {
                if (top < 0) top = y;
                continue;
            }

            if (top >= 0 && y - top >= 4 && y - top <= 22)
            {
                rows.Add((top, y));
            }

            top = -1;
        }

        return rows;
    }

    private static int[] LocalMean(byte[] bgra, int width, int height)
    {
        const int radius = 6;
        var sat = new long[(width + 1) * (height + 1)];
        var stride = width + 1;
        for (var y = 0; y < height; y++)
        {
            long row = 0;
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                row += (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
                sat[(y + 1) * stride + (x + 1)] = row + sat[y * stride + (x + 1)];
            }
        }

        var mean = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            var y0 = Math.Max(0, y - radius);
            var y1 = Math.Min(height - 1, y + radius);
            for (var x = 0; x < width; x++)
            {
                var x0 = Math.Max(0, x - radius);
                var x1 = Math.Min(width - 1, x + radius);
                var sum = sat[(y1 + 1) * stride + (x1 + 1)]
                    - sat[y0 * stride + (x1 + 1)]
                    - sat[(y1 + 1) * stride + x0]
                    + sat[y0 * stride + x0];
                var area = (x1 - x0 + 1) * (y1 - y0 + 1);
                mean[y * width + x] = (int)(sum / area);
            }
        }

        return mean;
    }

    private static byte[] KeepSmall(byte[] whiteInk, int width, int height, int maxHeight, int maxArea)
    {
        var seen = new bool[width * height];
        var queue = new Queue<int>();
        var drop = new bool[width * height];
        for (var start = 0; start < width * height; start++)
        {
            if (seen[start] || whiteInk[start * 4] == 0) continue;
            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var cells = new List<int>();
            var minY = height;
            var maxY = 0;
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                cells.Add(id);
                var x = id % width;
                var y = id / width;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                Try(x - 1, y);
                Try(x + 1, y);
                Try(x, y - 1);
                Try(x, y + 1);
            }

            if (maxY - minY + 1 > maxHeight || cells.Count > maxArea)
            {
                foreach (var id in cells) drop[id] = true;
            }

            void Try(int x, int y)
            {
                if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
                var id = y * width + x;
                if (seen[id] || whiteInk[id * 4] == 0) return;
                seen[id] = true;
                queue.Enqueue(id);
            }
        }

        var dest = new byte[whiteInk.Length];
        for (var i = 0; i < drop.Length; i++)
        {
            if (drop[i] || whiteInk[i * 4] == 0) continue;
            dest[i * 4] = 255;
            dest[i * 4 + 1] = 255;
            dest[i * 4 + 2] = 255;
            dest[i * 4 + 3] = 255;
        }

        return dest;
    }
}
