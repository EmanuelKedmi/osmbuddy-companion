namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// Candidate masks. Battle masks are black glyphs on white, matching
/// <see cref="MarketNotes.Companion.Scanning.BattleInk"/>. Loot masks are
/// white glyphs on black, matching <see cref="MarketNotes.Companion.Scanning.LootInk"/>.
/// "baseline" is the production rule as of the harness, copied here so a
/// later library change can still be scored against the old cut.
/// </summary>
static class Masks
{
    public static byte[] BattleBaseline(byte[] bgra, int width, int height) =>
        Paint(bgra, width, height, BattleBaselineInk, inkIsBlack: true);

    public static byte[] LootBaseline(byte[] bgra, int width, int height) =>
        Paint(bgra, width, height, LootBaselineInk, inkIsBlack: false);

    public static bool BattleBaselineInk(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        if (max > 96) return false;
        if (r > g && r > b + 8 && max > 40) return false;
        return true;
    }

    public static bool LootBaselineInk(byte r, byte g, byte b)
    {
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        if (min < 165) return false;
        if (max - min > 48) return false;
        if (g > r + 22 && g > b + 22) return false;
        return true;
    }

    public static byte[] DropTall(byte[] ink, int width, int height, int maxHeight, int minArea)
    {
        var keep = new bool[width * height];
        var seen = new bool[width * height];
        var queue = new Queue<int>();
        for (var start = 0; start < width * height; start++)
        {
            if (seen[start] || ink[start * 4] != 0) continue;
            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var cells = new List<int>();
            var minX = width;
            var maxX = 0;
            var minY = height;
            var maxY = 0;
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                cells.Add(id);
                var x = id % width;
                var y = id / width;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                Try(x - 1, y);
                Try(x + 1, y);
                Try(x, y - 1);
                Try(x, y + 1);
            }

            var tall = maxY - minY + 1 > maxHeight;
            if (!tall && cells.Count >= minArea)
            {
                foreach (var id in cells) keep[id] = true;
            }

            void Try(int x, int y)
            {
                if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
                var id = y * width + x;
                if (seen[id] || ink[id * 4] != 0) return;
                seen[id] = true;
                queue.Enqueue(id);
            }
        }

        var dest = new byte[ink.Length];
        for (var i = 0; i < keep.Length; i++)
        {
            var v = (byte)(keep[i] ? 0 : 255);
            dest[i * 4] = v;
            dest[i * 4 + 1] = v;
            dest[i * 4 + 2] = v;
            dest[i * 4 + 3] = 255;
        }

        return dest;
    }

    /// <summary>
    /// Thick blobs (squirrels, dirt) survive an opening. Thin glyph strokes do not.
    /// Subtracting the opened mask keeps the strokes, including where they touch a blob.
    /// </summary>
    public static byte[] KeepThin(byte[] ink, int width, int height, int radius)
    {
        var on = new bool[width * height];
        for (var i = 0; i < on.Length; i++) on[i] = ink[i * 4] == 0;
        var eroded = Erode(on, width, height, radius);
        var fat = Dilate(eroded, width, height, radius);
        var dest = new byte[ink.Length];
        for (var i = 0; i < on.Length; i++)
        {
            var v = (byte)(on[i] && !fat[i] ? 0 : 255);
            dest[i * 4] = v;
            dest[i * 4 + 1] = v;
            dest[i * 4 + 2] = v;
            dest[i * 4 + 3] = 255;
        }

        return dest;
    }

    public static byte[] BattleEdges(byte[] bgra, int width, int height)
    {
        var mean = LocalMean(bgra, width, height);
        var dest = new byte[bgra.Length];
        for (var i = 0; i < width * height; i++)
        {
            var r = bgra[i * 4 + 2];
            var g = bgra[i * 4 + 1];
            var b = bgra[i * 4];
            var lum = (r + g + b) / 3;
            var warm = r > g && r > b + 8 && Math.Max(r, Math.Max(g, b)) > 40;
            var green = g > r + 18 && g > b + 10 && g > 80;
            var darker = lum + 20 <= mean[i] && lum <= 150 && !warm && !green;
            var ink = BattleBaselineInk(r, g, b) || darker;
            var v = (byte)(ink ? 0 : 255);
            dest[i * 4] = v;
            dest[i * 4 + 1] = v;
            dest[i * 4 + 2] = v;
            dest[i * 4 + 3] = 255;
        }

        return DropTall(dest, width, height, 26, 4);
    }

    public static byte[] LootLocal(byte[] bgra, int width, int height)
    {
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
            var gold = r >= 180 && g is >= 90 and <= 210 && b <= 120 && r > g + 25;
            var neutral = max - min <= 70;
            var brightOnDark = min >= 150 && neutral && !green && !gold && lum >= mean[i] + 18;
            var grayOnSky = neutral && !green && !gold && mean[i] >= 165 && lum + 12 <= mean[i] && lum >= 110 && lum <= 210;
            if (!brightOnDark && !grayOnSky) continue;
            dest[i * 4] = 255;
            dest[i * 4 + 1] = 255;
            dest[i * 4 + 2] = 255;
            dest[i * 4 + 3] = 255;
        }

        return dest;
    }

    /// <summary>
    /// White glyphs on a dark map, and gray glyphs on a pale sky.
    /// A flat bright sky passes the old min&gt;=165 cut and swallows the letters.
    /// </summary>
    public static byte[] LootNotice(byte[] bgra, int width, int height)
    {
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
            var gold = r >= 180 && g is >= 90 and <= 210 && b <= 120 && r > g + 25;
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

    /// <summary>White loot ink. Drops components taller than a notice line or wider than a short phrase.</summary>
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

    /// <summary>Light glyph with a dark outline pixel within two steps. Drops flat sky.</summary>
    public static byte[] LootOutline(byte[] bgra, int width, int height)
    {
        var light = new bool[width * height];
        var dark = new bool[width * height];
        for (var i = 0; i < light.Length; i++)
        {
            var r = bgra[i * 4 + 2];
            var g = bgra[i * 4 + 1];
            var b = bgra[i * 4];
            var min = Math.Min(r, Math.Min(g, b));
            var max = Math.Max(r, Math.Max(g, b));
            light[i] = min >= 145 && max - min <= 60 && !(g > r + 22 && g > b + 22);
            dark[i] = max <= 90;
        }

        var dest = new byte[bgra.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var id = y * width + x;
                if (!light[id] || !NearDark(x, y)) continue;
                var at = id * 4;
                dest[at] = 255;
                dest[at + 1] = 255;
                dest[at + 2] = 255;
                dest[at + 3] = 255;
            }
        }

        return dest;

        bool NearDark(int x, int y)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                var yy = y + dy;
                if ((uint)yy >= (uint)height) continue;
                for (var dx = -2; dx <= 2; dx++)
                {
                    var xx = x + dx;
                    if ((uint)xx >= (uint)width) continue;
                    if (dark[yy * width + xx]) return true;
                }
            }

            return false;
        }
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

    private static bool[] Erode(bool[] on, int width, int height, int radius)
    {
        var dest = new bool[on.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var ok = true;
                for (var dy = -radius; dy <= radius && ok; dy++)
                {
                    var yy = y + dy;
                    if ((uint)yy >= (uint)height) { ok = false; break; }
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var xx = x + dx;
                        if ((uint)xx >= (uint)width || !on[yy * width + xx]) { ok = false; break; }
                    }
                }

                dest[y * width + x] = ok;
            }
        }

        return dest;
    }

    private static bool[] Dilate(bool[] on, int width, int height, int radius)
    {
        var dest = new bool[on.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var hit = false;
                for (var dy = -radius; dy <= radius && !hit; dy++)
                {
                    var yy = y + dy;
                    if ((uint)yy >= (uint)height) continue;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var xx = x + dx;
                        if ((uint)xx >= (uint)width) continue;
                        if (on[yy * width + xx]) { hit = true; break; }
                    }
                }

                dest[y * width + x] = hit;
            }
        }

        return dest;
    }

    private static byte[] Paint(byte[] bgra, int width, int height, Func<byte, byte, byte, bool> ink, bool inkIsBlack)
    {
        var dest = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var on = ink(bgra[i + 2], bgra[i + 1], bgra[i]);
            var v = (byte)(on == inkIsBlack ? 0 : 255);
            dest[i] = v;
            dest[i + 1] = v;
            dest[i + 2] = v;
            dest[i + 3] = 255;
        }

        return dest;
    }
}
