using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// Finds the client's item tooltip by the opaque pale square behind the item
/// icon. Every item tooltip has one, and its color does not depend on what is
/// behind the tooltip. The rest of the tooltip is a translucent navy, so a
/// pixel there is about 19% background over a fixed navy. The square has to
/// sit in that navy on all four sides. Measured at 64 px wide when the game
/// is at 100%; the box edges are offsets from the square in that unit.
/// </summary>
public static class ItemTooltipFinder
{
    public readonly record struct Hit(PixelRect Bounds, PixelRect Title, PixelRect Icon);

    private const double IconUnit = 64;
    private const double LeftFromIcon = 20;
    private const double TopFromIcon = 42;
    private const double MaxWidth = 380;
    private const double MaxHeight = 640;

    public static Hit? Find(byte[] bgra, int width, int height, int stride, int cursorX, int cursorY)
    {
        if (width < 64 || height < 64 || stride < width * 4 || bgra.Length < height * stride) return null;
        var x0 = Math.Clamp(cursorX - 620, 2, width - 3);
        var x1 = Math.Clamp(cursorX + 620, 2, width - 3);
        var y0 = Math.Clamp(cursorY - 700, 2, height - 3);
        var y1 = Math.Clamp(cursorY + 520, 2, height - 3);
        if (x1 - x0 < 40 || y1 - y0 < 40) return null;

        var image = new Image(bgra, width, height, stride);
        Hit? best = null;
        var bestDistance = long.MaxValue;
        for (var y = y0; y <= y1; y++)
        {
            var x = x0;
            while (x <= x1)
            {
                if (!image.Pale(x, y))
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x <= x1 && image.Pale(x, y)) x++;
                var length = x - start;
                if (length is < 36 or > 170) continue;
                var icon = Square(image, start, y, length);
                if (icon == null) continue;
                var hit = Box(image, icon.Value);
                if (hit == null) continue;
                var dx = (long)(hit.Value.Bounds.X - cursorX);
                var dy = (long)(hit.Value.Bounds.Y - cursorY);
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                best = hit;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// A pale run that is the top row of the icon square: navy above it,
    /// pale sides and bottom, and navy just outside the sides.
    /// </summary>
    private static PixelRect? Square(Image image, int x, int y, int length)
    {
        var scale = length / IconUnit;
        var reach = Math.Max(2, (int)Math.Round(2 * scale));
        if (x - reach < 0 || y - reach < 0 || x + length + reach >= image.Width) return null;
        if (image.NavyRow(y - reach, x + reach, x + length - reach) < 0.7) return null;

        var bottom = -1;
        var low = y + (int)(length * 0.85);
        var high = Math.Min(image.Height - reach - 1, y + (int)Math.Ceiling(length * 1.15));
        for (var row = high; row >= low; row--)
        {
            if (image.PaleRow(row, x + 1, x + length - 1) < 0.75) continue;
            bottom = row;
            break;
        }

        if (bottom < 0) return null;
        var size = bottom - y + 1;
        if (Math.Abs(size - length) > length * 0.12) return null;
        if (image.PaleColumn(x + 1, y, bottom) < 0.75) return null;
        if (image.PaleColumn(x + length - 2, y, bottom) < 0.75) return null;
        if (image.NavyColumn(x - reach, y + reach, bottom - reach) < 0.6) return null;
        if (image.NavyColumn(x + length - 1 + reach, y + reach, bottom - reach) < 0.5) return null;
        if (image.NavyRow(bottom + reach, x + reach, x + length - reach) < 0.5) return null;
        return new PixelRect(x, y, length, size);
    }

    /// <summary>
    /// The box around the square. Left and top are fixed offsets. Right and
    /// bottom are where the empty margin strips stop being navy: the strip
    /// above the title and the strip left of the text. Over a navy background
    /// the edge cannot be seen, so the right edge falls back to the text.
    /// </summary>
    private static Hit? Box(Image image, PixelRect icon)
    {
        var scale = icon.Width / IconUnit;
        int U(double units) => Math.Max(1, (int)Math.Round(units * scale));

        var left = icon.X - U(LeftFromIcon);
        var top = icon.Y - U(TopFromIcon);
        if (left < 0 || top < 0) return null;

        var marginTop = top + U(4);
        var marginBottom = top + U(11);
        var marginLeft = left + U(5);
        var marginRight = left + U(13);
        if (image.NavyBlock(marginLeft, marginRight, icon.Y, icon.Bottom - 1) < 0.75) return null;
        if (image.NavyBlock(marginLeft, icon.Right - 1, marginTop, marginBottom) < 0.75) return null;

        var rightCap = Math.Min(image.Width - 1, left + U(MaxWidth));
        var right = Edge(
            icon.Right + U(4),
            rightCap,
            U(4),
            x => image.NavyColumn(x, marginTop, marginBottom));
        if (right < 0)
        {
            var text = image.InkRight(icon.Right, rightCap, top + U(8), icon.Bottom - 1);
            right = Math.Min(rightCap, Math.Max(icon.Right + U(60), text + U(18)));
        }

        var bottomCap = Math.Min(image.Height - 1, top + U(MaxHeight));
        var bottom = Edge(
            icon.Bottom + U(4),
            bottomCap,
            U(4),
            y => image.NavyRow(y, marginLeft, marginRight));
        if (bottom < 0) bottom = Math.Min(bottomCap, icon.Bottom + U(30));

        var bounds = new PixelRect(left, top, right - left, bottom - top);
        var titleX = left + U(10);
        var titleY = top + U(12);
        var title = new PixelRect(titleX, titleY, Math.Max(1, right - U(6) - titleX), Math.Max(1, icon.Y - U(3) - titleY));
        return new Hit(bounds, title, icon);
    }

    /// <summary>
    /// The first position where most of the next <paramref name="window"/>
    /// steps are not navy. -1 when it stays navy.
    /// </summary>
    private static int Edge(int from, int to, int window, Func<int, double> navy)
    {
        for (var at = from; at + window <= to; at++)
        {
            if (navy(at) >= 0.3) continue;
            var outside = 0;
            for (var step = 0; step < window; step++)
            {
                if (navy(at + step) < 0.3) outside++;
            }

            if (outside * 4 >= window * 3) return at;
        }

        return -1;
    }

    private sealed class Image(byte[] bgra, int width, int height, int stride)
    {
        public int Width => width;
        public int Height => height;

        /// <summary>The opaque lavender-gray behind the item icon.</summary>
        public bool Pale(int x, int y)
        {
            var i = y * stride + x * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            return r + g + b >= 450 && max - min <= 50 && b + 12 >= r && b + 12 >= g;
        }

        /// <summary>
        /// Tooltip navy over any background: about 0.81 × (31, 31, 78)/0.81
        /// plus 0.19 × background, with room for JPEG and scaling.
        /// </summary>
        public bool Navy(int x, int y)
        {
            var i = y * stride + x * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            return r is >= 14 and <= 100
                && g is >= 14 and <= 100
                && b is >= 58 and <= 145
                && b >= Math.Max(r, g) + 4;
        }

        public bool Ink(int x, int y)
        {
            var i = y * stride + x * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            return r + g + b >= 390 || Math.Max(r, Math.Max(g, b)) >= 190;
        }

        public double PaleRow(int y, int x0, int x1) => Fraction(x0, x1, x => Pale(x, y));
        public double PaleColumn(int x, int y0, int y1) => Fraction(y0, y1, y => Pale(x, y));
        public double NavyRow(int y, int x0, int x1) => y < 0 || y >= height ? 0 : Fraction(x0, x1, x => Navy(x, y));
        public double NavyColumn(int x, int y0, int y1) => x < 0 || x >= width ? 0 : Fraction(y0, y1, y => Navy(x, y));

        public double NavyBlock(int x0, int x1, int y0, int y1)
        {
            if (x0 < 0 || y0 < 0 || x1 >= width || y1 >= height || x1 < x0 || y1 < y0) return 0;
            var hits = 0;
            var seen = 0;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    seen++;
                    if (Navy(x, y)) hits++;
                }
            }

            return hits / (double)seen;
        }

        public int InkRight(int x0, int x1, int y0, int y1)
        {
            for (var x = Math.Min(x1, width - 1); x >= x0; x--)
            {
                for (var y = Math.Max(0, y0); y <= Math.Min(y1, height - 1); y++)
                {
                    if (Ink(x, y)) return x;
                }
            }

            return x0;
        }

        private double Fraction(int from, int to, Func<int, bool> test)
        {
            if (to < from) return 0;
            var hits = 0;
            for (var at = from; at <= to; at++)
            {
                if (test(at)) hits++;
            }

            return hits / (double)(to - from + 1);
        }
    }
}
