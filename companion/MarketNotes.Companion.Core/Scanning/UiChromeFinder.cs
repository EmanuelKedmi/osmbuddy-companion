namespace MarketNotes.Companion.Scanning;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed class UiChrome
{
    public PixelRect? Minimap { get; init; }
    public PixelRect? MapTitle { get; init; }
    public PixelRect? Shop { get; init; }
    public PixelRect? ShopTitle { get; init; }
    public PixelRect? ShopRows { get; init; }
    public PixelRect? Scrollbar { get; init; }
    /// <summary>0 is the top of the track. Null when the thumb was not found.</summary>
    public double? ScrollPosition { get; init; }
    public required string Note { get; init; }
}

/// <summary>
/// Finds the minimap and the hired-merchant window from the pale blue-gray
/// frame used by this client. The border is a light steel line, not the old
/// wood chrome. The scrollbar is the narrow track beside the item list,
/// with an arrow button at each end. The pale preview beside it is wider
/// and is not the scrollbar.
/// </summary>
public static class UiChromeFinder
{
    public const byte FrameR = 160;
    public const byte FrameG = 178;
    public const byte FrameB = 198;
    public const byte TrackR = 232;
    public const byte TrackG = 232;
    public const byte TrackB = 232;
    public const byte ThumbR = 129;
    public const byte ThumbG = 147;
    public const byte ThumbB = 167;

    public static UiChrome Find(byte[] bgra, int width, int height, int stride, PixelRect? ignoreShop = null)
        => Find(bgra, width, height, stride, ignoreShop is PixelRect one ? new[] { one } : null);

    public static UiChrome Find(byte[] bgra, int width, int height, int stride, IReadOnlyList<PixelRect>? ignoreShops)
    {
        if (bgra.Length == 0 || width < 40 || height < 40 || stride < width * 4 || height * stride > bgra.Length)
        {
            return new UiChrome { Note = "The frame is too small to search for a window." };
        }

        var frames = FindFrames(bgra, width, height, stride);
        // A bright hunting map (Toy Factory) is as pale as a shop. Claim the
        // minimap first, or that map is stored as the shop and then ignored.
        FrameHit? mapHit = null;
        var maxMapW = Math.Max(280, width / 2);
        var maxMapH = Math.Max(260, height / 2);
        // The minimap sits in the upper-left. Pale shop chrome near the top
        // right was winning on Y alone, so Free Market never entered mode.
        var maxMapX = Math.Max(320, width * 45 / 100);
        foreach (var frame in frames)
        {
            if (frame.Bounds.Width < 80 || frame.Bounds.Width > maxMapW) continue;
            if (frame.Bounds.Height < 70 || frame.Bounds.Height > maxMapH) continue;
            if (frame.Bounds.Y > height * 0.45) continue;
            if (frame.Bounds.X > maxMapX) continue;
            if (frame.Variance < 8) continue;
            if (mapHit == null
                || frame.Bounds.Y < mapHit.Value.Bounds.Y - 10
                || (Math.Abs(frame.Bounds.Y - mapHit.Value.Bounds.Y) <= 10 && frame.Bounds.X < mapHit.Value.Bounds.X))
            {
                mapHit = frame;
            }
        }

        FrameHit? shopHit = null;
        var shopScore = int.MinValue;
        foreach (var frame in frames)
        {
            if (mapHit != null && Overlaps(frame.Bounds, mapHit.Value.Bounds)) continue;
            if (ignoreShops != null && ignoreShops.Any(skip => Overlaps(frame.Bounds, skip))) continue;
            if (frame.Bounds.Width < 220 || frame.Bounds.Height < 160) continue;
            if (frame.MeanLum < 165) continue;
            // Default hired-merchant placement sits beside a larger empty
            // character/chat pale panel. Prefer the list (scrollbar + texture)
            // over the biggest blank window.
            var score = ShopScore(frame);
            if (shopHit == null || score > shopScore)
            {
                shopHit = frame;
                shopScore = score;
            }
        }

        var note = new List<string>();
        PixelRect? minimap = null;
        PixelRect? mapTitle = null;
        if (mapHit == null)
        {
            note.Add("Minimap frame not found.");
        }
        else
        {
            minimap = mapHit.Value.Bounds;
            mapTitle = NameBand(bgra, stride, minimap.Value);
            note.Add($"Minimap {mapHit.Value.Bounds.X},{mapHit.Value.Bounds.Y} {mapHit.Value.Bounds.Width}×{mapHit.Value.Bounds.Height}.");
        }

        PixelRect? shop = null;
        PixelRect? shopTitle = null;
        PixelRect? shopRows = null;
        PixelRect? scrollbar = null;
        double? scrollPosition = null;
        if (shopHit == null)
        {
            note.Add("Shop frame not found.");
        }
        else
        {
            shop = shopHit.Value.Bounds;
            shopTitle = shopHit.Value.Title;
            var scroll = shopHit.Value.Scroll;
            var rowsLeft = shop.Value.X + 8;
            var rowsTop = shopTitle.Value.Bottom + 2;
            var rowsBottom = shop.Value.Bottom - 6;
            int rowsRight;
            if (scroll != null)
            {
                scrollbar = scroll.Value.Bounds;
                scrollPosition = scroll.Value.Position;
                rowsRight = scroll.Value.Bounds.X - 2;
                if (scroll.Value.Capped)
                {
                    // The up and down buttons are the ends of the visible list.
                    rowsTop = scroll.Value.Bounds.Y;
                    rowsBottom = scroll.Value.Bounds.Bottom;
                }
                else
                {
                    // A thumb-only track can start below the first cards.
                    rowsTop = ListTop(bgra, stride, rowsLeft, rowsRight, shopTitle.Value.Bottom + 2, scroll.Value.Bounds.Y);
                    rowsBottom = scroll.Value.Bounds.Bottom;
                }
            }
            else
            {
                // Without a scrollbar hit, still OCR most of the list. The old
                // 48% cut assumed a pale preview column and left prices out.
                rowsRight = shop.Value.Right - 18;
            }

            if (rowsRight - rowsLeft >= 40 && rowsBottom - rowsTop >= 20)
            {
                shopRows = new PixelRect(rowsLeft, rowsTop, rowsRight - rowsLeft, rowsBottom - rowsTop);
            }

            var thumb = scroll == null
                ? "scrollbar not found"
                : scroll.Value.Position == null
                    ? "scrollbar found, thumb not read"
                    : $"thumb {scroll.Value.Position.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";
            note.Add($"Shop frame {shop.Value.X},{shop.Value.Y} {shop.Value.Width}×{shop.Value.Height}. {thumb}.");
        }

        return new UiChrome
        {
            Minimap = minimap,
            MapTitle = mapTitle,
            Shop = shop,
            ShopTitle = shopTitle,
            ShopRows = shopRows,
            Scrollbar = scrollbar,
            ScrollPosition = scrollPosition,
            Note = string.Join(" ", note),
        };
    }

    /// <summary>
    /// The town and the map name are the lines just above the rule that
    /// separates them from the map canvas. The button row sits higher.
    /// </summary>
    private static PixelRect NameBand(byte[] bgra, int stride, PixelRect map)
    {
        var divider = NameDivider(bgra, stride, map);
        int nameTop;
        int nameBottom;
        if (divider is int line && line - map.Y >= 36)
        {
            var band = Math.Clamp((line - map.Y) * 62 / 100, 40, 150);
            nameBottom = line - 2;
            nameTop = Math.Max(map.Y + 4, nameBottom - band);
        }
        else
        {
            nameTop = map.Y + 16;
            var span = Math.Max(48, Math.Min(160, map.Height * 35 / 100));
            nameBottom = Math.Min(map.Bottom - 8, nameTop + span);
        }

        var height = Math.Max(16, Math.Min(nameBottom - nameTop, map.Bottom - nameTop - 4));
        return new PixelRect(map.X + 4, nameTop, Math.Max(8, map.Width - 8), height);
    }

    private static int? NameDivider(byte[] bgra, int stride, PixelRect map)
    {
        var start = map.Y + 24;
        var end = Math.Min(map.Bottom - 16, map.Y + Math.Max(48, map.Height * 55 / 100));
        int? found = null;
        var need = Math.Max(40, map.Width * 7 / 10);
        for (var y = start; y < end; y++)
        {
            var hit = 0;
            for (var x = map.X + 2; x < map.Right - 2; x++)
            {
                if (IsFrame(bgra, stride, x, y)) hit++;
            }

            if (hit >= need) found = y;
        }

        return found;
    }

    /// <summary>
    /// Every pale frame on this client, including the shop and the minimap.
    /// The stat window and the skill window use that same border. Callers
    /// tell them apart from the text inside, not from which frame is largest.
    /// </summary>
    public static IReadOnlyList<PixelRect> Frames(byte[] bgra, int width, int height, int stride)
    {
        if (bgra.Length == 0 || width < 40 || height < 40 || stride < width * 4 || height * stride > bgra.Length)
        {
            return [];
        }

        return FindFrames(bgra, width, height, stride).Select(frame => frame.Bounds).ToList();
    }

    private readonly record struct HRun(int Y, int X0, int X1);

    private static List<HRun> MergeGaps(List<HRun> runs, int maxGap)
    {
        var merged = new List<HRun>();
        foreach (var group in runs.GroupBy(run => run.Y))
        {
            HRun? current = null;
            foreach (var run in group.OrderBy(run => run.X0))
            {
                if (current == null)
                {
                    current = run;
                    continue;
                }

                if (run.X0 - current.Value.X1 <= maxGap)
                {
                    current = current.Value with { X1 = Math.Max(current.Value.X1, run.X1) };
                }
                else
                {
                    merged.Add(current.Value);
                    current = run;
                }
            }

            if (current != null) merged.Add(current.Value);
        }

        return merged;
    }

    private readonly record struct ScrollHit(PixelRect Bounds, double? Position, bool Capped);

    private readonly record struct FrameHit(PixelRect Bounds, PixelRect Title, int Variance, int MeanLum, ScrollHit? Scroll);

    private static List<FrameHit> FindFrames(byte[] bgra, int width, int height, int stride)
    {
        var runs = new List<HRun>();
        for (var y = 0; y < height; y++)
        {
            var x = 0;
            while (x < width)
            {
                if (!IsFrame(bgra, stride, x, y))
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < width && IsFrame(bgra, stride, x, y)) x++;
                if (x - start >= 70) runs.Add(new HRun(y, start, x - 1));
            }
        }

        runs = MergeGaps(runs, Math.Max(12, width / 80));

        var found = new List<FrameHit>();
        foreach (var top in runs)
        {
            HRun? bottom = null;
            var topLen = top.X1 - top.X0 + 1;
            foreach (var candidate in runs)
            {
                var gap = candidate.Y - top.Y;
                var minGap = Math.Max(70, height / 8);
                var maxGap = Math.Max(460, (height * 3) / 4);
                if (gap < minGap || gap > maxGap) continue;
                var botLen = candidate.X1 - candidate.X0 + 1;
                var overlap = Math.Min(top.X1, candidate.X1) - Math.Max(top.X0, candidate.X0);
                var shorter = Math.Min(topLen, botLen);
                var longer = Math.Max(topLen, botLen);
                if (shorter < 70 || overlap < shorter * 0.75 || longer > shorter * 1.25) continue;
                // The name plate has its own line. The window bottom is the lower one.
                if (bottom == null || candidate.Y > bottom.Value.Y) bottom = candidate;
            }

            if (bottom == null) continue;
            var left = Math.Max(top.X0, bottom.Value.X0);
            var right = Math.Min(top.X1, bottom.Value.X1);
            var bounds = new PixelRect(left, top.Y, right - left + 1, bottom.Value.Y - top.Y + 1);
            if (found.Any(existing => Overlaps(existing.Bounds, bounds) && existing.Bounds.Width * existing.Bounds.Height >= bounds.Width * bounds.Height))
            {
                continue;
            }

            found.RemoveAll(existing => Overlaps(existing.Bounds, bounds));
            var titleBottom = top.Y + 22;
            foreach (var inner in runs)
            {
                var gap = inner.Y - top.Y;
                if (gap < 16 || gap > Math.Max(42, height / 14)) continue;
                var overlap = Math.Min(top.X1, inner.X1) - Math.Max(top.X0, inner.X0);
                if (overlap > (top.X1 - top.X0) * 0.7)
                {
                    titleBottom = inner.Y;
                    break;
                }
            }

            var titleHeight = Math.Max(12, titleBottom - top.Y - 2);
            var title = new PixelRect(left + 4, top.Y + 3, Math.Max(8, bounds.Width - 8), Math.Min(titleHeight, bounds.Height - 8));
            var interiorTop = title.Bottom + 2;
            var interiorBottom = bounds.Bottom - 4;
            var variance = InteriorStat(bgra, stride, left + 6, interiorTop, right - 6, interiorBottom, out var mean);
            var scroll = FindScroll(bgra, stride, left + 8, right - 8, interiorTop, interiorBottom);
            found.Add(new FrameHit(bounds, title, variance, mean, scroll));
        }

        return found;
    }

    /// <summary>
    /// The scrollbar track can begin below the first item cards. The cards
    /// share one fill. Walk up through that fill and stop at the header.
    /// </summary>
    private static int ListTop(byte[] bgra, int stride, int left, int right, int floor, int scrollbarTop)
    {
        if (right - left < 40 || scrollbarTop <= floor + 4) return scrollbarTop;
        var sampleY = scrollbarTop + 16;
        if (!TryRowTone(bgra, stride, left, right, sampleY, out var lum, out var cast)) return scrollbarTop;
        var mismatched = 0;
        var cursor = scrollbarTop;
        var sawHeader = false;
        var limit = Math.Max(floor, scrollbarTop - 170);
        for (var y = scrollbarTop - 1; y >= limit; y--)
        {
            if (TryRowTone(bgra, stride, left, right, y, out var rowLum, out var rowCast)
                && Math.Abs(rowLum - lum) <= 24
                && Math.Abs(rowCast - cast) <= 18)
            {
                mismatched = 0;
                cursor = y;
                continue;
            }

            if (++mismatched > 12)
            {
                sawHeader = true;
                break;
            }
        }

        return sawHeader ? cursor : scrollbarTop;
    }

    private static bool TryRowTone(byte[] bgra, int stride, int left, int right, int y, out int lum, out int cast)
    {
        lum = 0;
        cast = 0;
        var x0 = left + (right - left) / 5;
        var x1 = right - 6;
        if (x1 - x0 < 12) return false;
        var lums = new List<int>();
        var casts = new List<int>();
        for (var x = x0; x < x1; x += 3)
        {
            if (!TryPixel(bgra, stride, x, y, out var b, out var g, out var r)) continue;
            var value = (r + g + b) / 3;
            if (value < 150) continue;
            lums.Add(value);
            casts.Add(b - r);
        }

        if (lums.Count < 6) return false;
        lums.Sort();
        casts.Sort();
        lum = lums[lums.Count / 2];
        cast = casts[casts.Count / 2];
        return true;
    }

    private readonly record struct BarColumn(bool Arrow, bool Thumb, int Top, int Bottom, int ThumbTop, int ThumbLen);

    /// <summary>
    /// The list scrollbar is a narrow column. Arrow buttons cap it, and the
    /// thumb, when it is a different color, sits on the track between them.
    /// A wide pale field such as the preview is not a scrollbar.
    /// </summary>
    private static ScrollHit? FindScroll(byte[] bgra, int stride, int left, int right, int top, int bottom)
    {
        var height = bottom - top;
        if (height < 40 || right - left < 40) return null;

        var capped = FindArrowScrollbar(bgra, stride, left, right, top, bottom);
        if (capped != null) return capped;

        var columns = new BarColumn[right - left + 1];
        for (var x = left; x <= right; x++)
        {
            columns[x - left] = MeasureColumn(bgra, stride, x, top, bottom);
        }

        ScrollHit? best = null;
        var bestScore = 0;
        var index = 0;
        while (index < columns.Length)
        {
            if (!columns[index].Arrow && !columns[index].Thumb)
            {
                index++;
                continue;
            }

            var start = index;
            var arrow = columns[index].Arrow;
            while (index < columns.Length && columns[index].Arrow == arrow && (columns[index].Arrow || columns[index].Thumb))
            {
                index++;
            }

            var width = index - start;
            if (width is < 4 or > 32) continue;

            var runLeft = left + start;
            var sample = columns[start + (width / 2)];
            for (var i = start; i < index; i++)
            {
                if (columns[i].Bottom - columns[i].Top > sample.Bottom - sample.Top) sample = columns[i];
            }

            var score = sample.Bottom - sample.Top;
            if (sample.Arrow) score += 1000;
            if (sample.Thumb) score += 200;
            if (score <= bestScore) continue;
            bestScore = score;

            var trackTop = sample.Top;
            var trackBottom = sample.Bottom;
            var boundsTop = trackTop;
            var boundsBottom = trackBottom;
            if (sample.Arrow)
            {
                boundsTop = ExtendArrow(bgra, stride, runLeft, runLeft + width - 1, trackTop, -1, top);
                boundsBottom = ExtendArrow(bgra, stride, runLeft, runLeft + width - 1, trackBottom, 1, bottom);
            }

            double? position = null;
            var trackHeight = trackBottom - trackTop;
            if (sample.Thumb && sample.ThumbLen >= 8 && sample.ThumbLen < trackHeight - 8)
            {
                var room = trackHeight - sample.ThumbLen;
                position = room <= 0 ? 0 : Math.Clamp((sample.ThumbTop - trackTop) / (double)room, 0, 1);
            }

            best = new ScrollHit(
                new PixelRect(runLeft, boundsTop, Math.Max(6, width), Math.Max(8, boundsBottom - boundsTop)),
                position,
                false);
        }

        return best;
    }

    /// <summary>
    /// The list length is the up button to the down button. A pale run in
    /// the middle is shorter whenever the thumb, or a card edge, breaks
    /// that color, so the buttons are the measurement.
    /// </summary>
    private static ScrollHit? FindArrowScrollbar(byte[] bgra, int stride, int left, int right, int top, int bottom)
    {
        var buttons = ArrowButtons(bgra, stride, left, right, top, bottom);
        ScrollHit? best = null;
        var bestGap = 0;
        for (var i = 0; i < buttons.Count; i++)
        {
            for (var j = 0; j < buttons.Count; j++)
            {
                if (i == j) continue;
                var upper = buttons[i];
                var lower = buttons[j];
                if (lower.Y <= upper.Bottom) continue;
                var gapTop = upper.Bottom + 1;
                var gap = lower.Y - gapTop;
                if (gap < 48 || gap <= bestGap) continue;
                var overlapLeft = Math.Max(upper.X, lower.X);
                var overlapRight = Math.Min(upper.Right, lower.Right);
                var overlap = overlapRight - overlapLeft + 1;
                if (overlap < 4) continue;
                var narrower = Math.Min(upper.Width, lower.Width);
                if (overlap * 2 < narrower) continue;
                if (!TrackBetween(bgra, stride, overlapLeft, overlapRight, gapTop, lower.Y)) continue;

                var position = ThumbBetween(bgra, stride, overlapLeft, overlapRight, gapTop, lower.Y);
                var bounds = new PixelRect(overlapLeft, upper.Y, Math.Max(6, overlap), lower.Bottom - upper.Y + 1);
                bestGap = gap;
                best = new ScrollHit(bounds, position, true);
            }
        }

        return best;
    }

    private readonly record struct ArrowButton(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width - 1;
        public int Bottom => Y + Height - 1;
    }

    private static List<ArrowButton> ArrowButtons(byte[] bgra, int stride, int left, int right, int top, int bottom)
    {
        var runs = new List<(int X, int Top, int Bottom)>();
        for (var x = left; x <= right; x++)
        {
            var runTop = -1;
            for (var y = top; y <= bottom; y++)
            {
                var arrow = y < bottom && IsArrow(bgra, stride, x, y);
                if (arrow)
                {
                    if (runTop < 0) runTop = y;
                }
                else if (runTop >= 0)
                {
                    if (y - runTop is >= 8 and <= 40) runs.Add((x, runTop, y - 1));
                    runTop = -1;
                }
            }
        }

        var used = new bool[runs.Count];
        var buttons = new List<ArrowButton>();
        for (var i = 0; i < runs.Count; i++)
        {
            if (used[i]) continue;
            var x0 = runs[i].X;
            var x1 = runs[i].X;
            var y0 = runs[i].Top;
            var y1 = runs[i].Bottom;
            used[i] = true;
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var j = 0; j < runs.Count; j++)
                {
                    if (used[j]) continue;
                    var run = runs[j];
                    if (run.X < x0 - 1 || run.X > x1 + 1) continue;
                    var overlap = Math.Min(y1, run.Bottom) - Math.Max(y0, run.Top) + 1;
                    var split = run.Top > y1 ? run.Top - y1 - 1 : y0 > run.Bottom ? y0 - run.Bottom - 1 : 0;
                    if (overlap < 6 && split > 6) continue;
                    used[j] = true;
                    x0 = Math.Min(x0, run.X);
                    x1 = Math.Max(x1, run.X);
                    y0 = Math.Min(y0, run.Top);
                    y1 = Math.Max(y1, run.Bottom);
                    grew = true;
                }
            }

            var width = x1 - x0 + 1;
            var height = y1 - y0 + 1;
            if (width is < 4 or > 28 || height is < 8 or > 44) continue;
            buttons.Add(new ArrowButton(x0, y0, width, height));
        }

        return buttons;
    }

    private static bool TrackBetween(byte[] bgra, int stride, int left, int right, int top, int bottom)
    {
        var best = 0;
        var most = 0;
        var step = Math.Max(1, (right - left) / 2);
        for (var x = left; x <= right; x += step)
        {
            var hit = 0;
            var count = 0;
            for (var y = top; y < bottom; y += 2)
            {
                count++;
                if (IsTrack(bgra, stride, x, y) || IsThumb(bgra, stride, x, y)) hit++;
            }

            most = Math.Max(most, count);
            if (count > 0) best = Math.Max(best, hit * 100 / count);
        }

        return most >= 16 && best >= 55;
    }

    private static double? ThumbBetween(byte[] bgra, int stride, int left, int right, int top, int bottom)
    {
        var bestTop = -1;
        var bestLen = 0;
        var gap = bottom - top;
        for (var x = left; x <= right; x++)
        {
            var run = -1;
            for (var y = top; y <= bottom; y++)
            {
                var thumb = y < bottom && IsThumb(bgra, stride, x, y);
                if (thumb)
                {
                    if (run < 0) run = y;
                }
                else if (run >= 0)
                {
                    var len = y - run;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestTop = run;
                    }

                    run = -1;
                }
            }
        }

        if (bestTop < 0 || bestLen < 8 || bestLen >= gap - 8) return null;
        var room = gap - bestLen;
        return room <= 0 ? 0 : Math.Clamp((bestTop - top) / (double)room, 0, 1);
    }

    private static BarColumn MeasureColumn(byte[] bgra, int stride, int x, int top, int bottom)
    {
        var runTop = -1;
        var bestTop = -1;
        var bestLen = 0;
        for (var y = top; y <= bottom; y++)
        {
            var bar = y < bottom && IsBar(bgra, stride, x, y);
            if (bar)
            {
                if (runTop < 0) runTop = y;
            }
            else if (runTop >= 0)
            {
                var len = y - runTop;
                if (len > bestLen)
                {
                    bestLen = len;
                    bestTop = runTop;
                }

                runTop = -1;
            }
        }

        if (bestTop < 0 || bestLen < 48) return default;
        var trackBottom = bestTop + bestLen;
        var arrow = ArrowCap(bgra, stride, x, bestTop, -1, top) && ArrowCap(bgra, stride, x, trackBottom, 1, bottom);
        var thumbTop = -1;
        var thumbLen = 0;
        var thumbRun = -1;
        for (var y = bestTop; y <= trackBottom; y++)
        {
            var thumb = y < trackBottom && IsThumb(bgra, stride, x, y);
            if (thumb)
            {
                if (thumbRun < 0) thumbRun = y;
            }
            else if (thumbRun >= 0)
            {
                var len = y - thumbRun;
                if (len > thumbLen)
                {
                    thumbLen = len;
                    thumbTop = thumbRun;
                }

                thumbRun = -1;
            }
        }

        var hasThumb = thumbTop >= 0 && thumbLen >= 8 && thumbLen < bestLen - 8;
        if (!arrow && !hasThumb) return default;
        return new BarColumn(arrow, hasThumb, bestTop, trackBottom, thumbTop, thumbLen);
    }

    private static bool ArrowCap(byte[] bgra, int stride, int x, int edge, int direction, int limit)
    {
        var hits = 0;
        for (var step = 1; step <= 22; step++)
        {
            var y = edge + (direction * step);
            if (direction < 0 && y < limit) break;
            if (direction > 0 && y >= limit) break;
            if (IsArrow(bgra, stride, x, y) || IsArrow(bgra, stride, x - 1, y) || IsArrow(bgra, stride, x + 1, y)) hits++;
        }

        return hits >= 6;
    }

    private static int ExtendArrow(byte[] bgra, int stride, int left, int right, int edge, int direction, int limit)
    {
        var cursor = edge;
        var missed = 0;
        for (var step = 1; step <= 28; step++)
        {
            var y = edge + (direction * step);
            if (direction < 0 && y < limit) break;
            if (direction > 0 && y >= limit) break;
            var arrow = false;
            for (var x = left; x <= right; x++)
            {
                if (IsArrow(bgra, stride, x, y))
                {
                    arrow = true;
                    break;
                }
            }

            if (arrow)
            {
                cursor = y;
                missed = 0;
            }
            else if (++missed > 3)
            {
                break;
            }
        }

        return cursor;
    }

    private static bool IsBar(byte[] bgra, int stride, int x, int y)
    {
        return IsTrack(bgra, stride, x, y) || IsThumb(bgra, stride, x, y);
    }

    private static int InteriorStat(byte[] bgra, int stride, int left, int top, int right, int bottom, out int meanLum)
    {
        meanLum = 0;
        if (right <= left || bottom <= top) return 0;
        long count = 0;
        long sum = 0;
        long sumSq = 0;
        for (var y = top; y < bottom; y += 3)
        {
            for (var x = left; x < right; x += 3)
            {
                var lum = Lum(bgra, stride, x, y);
                count++;
                sum += lum;
                sumSq += lum * lum;
            }
        }

        if (count < 8) return 0;
        var mean = sum / (double)count;
        meanLum = (int)mean;
        var variance = sumSq / (double)count - mean * mean;
        if (variance < 0) return 0;
        return (int)Math.Sqrt(variance);
    }

    private static int ShopScore(FrameHit frame)
    {
        var area = frame.Bounds.Width * frame.Bounds.Height;
        var score = area;
        if (frame.Scroll != null) score += 400_000;
        // Empty character/chat panels are bright white with almost no texture.
        if (frame.MeanLum >= 210 && frame.Variance <= 25) score -= 350_000;
        else if (frame.Variance >= 30) score += Math.Min(80_000, frame.Variance * 800);
        return score;
    }

    private static bool IsFrame(byte[] bgra, int stride, int x, int y)
    {
        if (!TryPixel(bgra, stride, x, y, out var b, out var g, out var r)) return false;
        // Default merchant chrome is a slightly warmer steel (R≈204) than the
        // character/chat panel beside it. The old R≤200 cut missed that border,
        // so only the empty right panel registered as the shop.
        if (r is < 120 or > 215) return false;
        if (g is < 140 or > 225) return false;
        if (b is < 160 or > 240) return false;
        if (b < r + 12) return false;
        if (g + 12 < r) return false;
        if (b + 15 < g) return false;
        var lum = (r + g + b) / 3;
        return lum is >= 145 and <= 225;
    }

    private static bool IsTrack(byte[] bgra, int stride, int x, int y)
    {
        if (!TryPixel(bgra, stride, x, y, out var b, out var g, out var r)) return false;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lum = (r + g + b) / 3;
        // Gray test tracks and the pink list track. The white page behind them is brighter.
        return max - min <= 46 && lum is >= 168 and <= 242;
    }

    private static bool IsArrow(byte[] bgra, int stride, int x, int y)
    {
        if (!TryPixel(bgra, stride, x, y, out var b, out var g, out var r)) return false;
        var lum = (r + g + b) / 3;
        if (lum is < 60 or > 145) return false;
        return b + 6 >= r && b + 12 >= g;
    }

    private static bool IsThumb(byte[] bgra, int stride, int x, int y)
    {
        if (!TryPixel(bgra, stride, x, y, out var b, out var g, out var r)) return false;
        var lum = (r + g + b) / 3;
        return lum is >= 140 and <= 200 && b >= r + 8 && b + 6 >= g;
    }

    private static int Lum(byte[] bgra, int stride, int x, int y)
    {
        if (!TryPixel(bgra, stride, x, y, out var b, out var g, out var r)) return 0;
        return (r + g + b) / 3;
    }

    private static bool TryPixel(byte[] bgra, int stride, int x, int y, out byte b, out byte g, out byte r)
    {
        b = g = r = 0;
        if (x < 0 || y < 0) return false;
        var index = y * stride + x * 4;
        if ((uint)index + 2 >= (uint)bgra.Length) return false;
        if (x * 4 >= stride) return false;
        b = bgra[index];
        g = bgra[index + 1];
        r = bgra[index + 2];
        return true;
    }

    private static bool Overlaps(PixelRect a, PixelRect b)
    {
        var x = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        var y = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
        var inter = x * y;
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller > 0 && inter * 2 > smaller;
    }
}
