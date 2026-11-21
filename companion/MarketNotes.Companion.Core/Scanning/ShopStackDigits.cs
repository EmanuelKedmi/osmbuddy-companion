namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Hired-merchant stack counts are painted on the item icon (bottom-left),
/// white/gray fill with a dark outline. Windows OCR never emits them, so the
/// listing parser would leave quantity at 1. This reads those pixels from the
/// shop-rows crop after OCR has priced the rows.
/// </summary>
public static class ShopStackDigits
{
    /// <summary>
    /// Overwrite each row's quantity when the icon digits are readable.
    /// Leaves the existing value (usually 1) when the crop is empty or sold
    /// rows wash the digits out.
    /// </summary>
    public static void Apply(byte[] bgra, int width, int height, int stride, IList<FmVisibleRow> rows)
    {
        if (rows.Count == 0 || width < 24 || height < rows.Count * 8 || stride < width * 4) return;
        var band = height / (double)rows.Count;
        for (var i = 0; i < rows.Count; i++)
        {
            var y0 = (int)(i * band);
            var y1 = Math.Max(y0 + 1, (int)((i + 1) * band));
            var qty = ReadBand(bgra, width, height, stride, y0, y1);
            if (qty is >= 0 and <= 9999) rows[i].Quantity = qty.Value;
        }
    }

    /// <summary>Read one stack count from a single shop-row band.</summary>
    public static int? ReadBand(byte[] bgra, int width, int height, int stride, int y0, int y1)
    {
        var bandH = y1 - y0;
        if (bandH < 10) return null;
        // Digits sit on the icon bottom-left. Three-digit stacks need ~34px.
        var cropY0 = y0 + bandH * 55 / 100;
        var cropY1 = Math.Min(y1 - 2, y0 + bandH * 92 / 100);
        var cropH = cropY1 - cropY0;
        var cropW = Math.Min(34, width);
        if (cropH < 8 || cropW < 6) return null;

        var mask = BuildMask(bgra, width, height, stride, 0, cropY0, cropW, cropH);
        ClearBottomStreaks(mask, cropW, cropH);
        var comps = SelectDigitComps(mask, cropW, cropH);
        // A lone ones-digit far right, or a tall blob that swallowed scroll art
        // onto a stem, needs the crown chopped before reselect.
        if (comps.Count == 1 && (comps[0].X0 >= 12 || (comps[0].H >= 18 && comps[0].W >= 10)))
        {
            DetachScrollArt(mask, cropW, cropH);
            for (var x = 0; x < cropW; x++) mask[x, cropH - 1] = false;
            comps = SelectDigitComps(mask, cropW, cropH);
            // After chopping, prefer the tallest thin stem if art left crumbs.
            if (comps.Count > 1)
            {
                var stem = comps.Where(c => c.W <= 6 && c.H >= 8)
                    .OrderByDescending(c => c.H).ThenBy(c => c.X0).ToList();
                if (stem.Count >= 1) comps = new List<Comp> { stem[0] };
            }
        }

        if (comps.Count == 0) return null;
        if (comps.Count > 3) comps = comps.Take(3).ToList();

        var digits = new List<int>(comps.Count);
        foreach (var c in comps)
        {
            var d = Classify(mask, c);
            if (d < 0) return null;
            digits.Add(d);
        }

        var value = 0;
        foreach (var d in digits) value = value * 10 + d;
        return value is >= 0 and <= 9999 ? value : null;
    }

    private static bool[,] BuildMask(byte[] bgra, int width, int height, int stride, int x0, int y0, int w, int h)
    {
        var m = new bool[w, h];
        for (var y = 0; y < h; y++)
        {
            var gy = y0 + y;
            if ((uint)gy >= (uint)height) continue;
            for (var x = 0; x < w; x++)
            {
                var gx = x0 + x;
                if ((uint)gx >= (uint)width) continue;
                var i = gy * stride + gx * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                if (!IsDigitFill(r, g, b)) continue;
                if (!HasDarkNeighbor(bgra, width, height, stride, gx, gy)) continue;
                m[x, y] = true;
            }
        }

        return m;
    }

    private static List<Comp> SelectDigitComps(bool[,] mask, int cropW, int cropH)
    {
        var sepFloor = cropH * 92 / 100;
        var comps = Components(mask, cropW, cropH)
            .Where(c => c.H >= 5 && c.H <= 22 && c.W <= 14 && c.Pixels >= 5 && c.Pixels <= 140)
            .Where(c => c.Y0 > 0 && c.Y0 < sepFloor && c.Y1 < cropH) // may touch last row
            .Where(c => c.W * 3 <= c.H * 4)
            .OrderBy(c => c.X0)
            .ToList();
        comps = Glue(comps);
        // Reject monster merges (digit + ore shine) that blow past a digit width.
        comps = comps.Where(c => c.W <= 14).ToList();
        if (comps.Count == 0) return comps;
        // Digits share a baseline; ignore short divider crumbs when picking it.
        // Three-digit stacks (100) reach past x=18.
        var digitLike = comps.Where(c => c.H >= 7 && c.W <= 13).ToList();
        var baseY = (digitLike.Count > 0 ? digitLike : comps).Max(c => c.Y1);
        comps = comps.Where(c => c.H >= 7 && c.Y1 >= baseY - 5 && c.X0 <= 26).ToList();
        // Ore frame chrome is a thin strip at x=0; drop it when a real stem follows.
        if (comps.Count >= 2 && comps[0].X0 == 0 && comps[0].W <= 3
            && comps[1].W <= 7 && comps[1].H >= comps[0].H - 2)
            comps.RemoveAt(0);
        return comps;
    }

    /// <summary>
    /// Row rules under the icon are bright streaks that glue onto digit feet.
    /// Only clear the bottom of the crop so digit crowns stay intact.
    /// </summary>
    private static void ClearBottomStreaks(bool[,] m, int w, int h)
    {
        var y0 = h * 85 / 100;
        for (var y = y0; y < h; y++)
        {
            var on = 0;
            for (var x = 0; x < w; x++) if (m[x, y]) on++;
            if (on < w / 2) continue;
            for (var x = 0; x < w; x++) m[x, y] = false;
        }
    }

    /// <summary>
    /// Scroll ornaments hang from the top of the crop and glue onto a "4" stem.
    /// Only wide top-hanging crowns are chopped; ore shine is left alone.
    /// </summary>
    private static void DetachScrollArt(bool[,] m, int w, int h)
    {
        foreach (var c in Components(m, w, h))
        {
            if (c.Y0 > 1 || c.H <= 18 || c.W < 14) continue;
            var keepFrom = Math.Max(c.Y0, c.Y1 - 12);
            for (var y = c.Y0; y < keepFrom; y++)
                for (var x = c.X0; x <= c.X1; x++)
                    m[x, y] = false;
        }
    }

    private static bool IsDigitFill(int r, int g, int b)
    {
        var lum = (r + g + b) / 3;
        if (lum < 150) return false;
        // Scroll parchment is warm; stack digits are cool/neutral gray-white.
        if (r > b + 35 && r > g + 20) return false;
        var cool = b >= r - 12;
        var neutral = Math.Abs(r - g) <= 40 && Math.Abs(g - b) <= 40;
        return cool || neutral;
    }

    private static bool HasDarkNeighbor(byte[] bgra, int width, int height, int stride, int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx;
                var ny = y + dy;
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;
                var j = ny * stride + nx * 4;
                if ((bgra[j] + bgra[j + 1] + bgra[j + 2]) / 3 <= 85) return true;
            }
        }

        return false;
    }

    private sealed class Comp
    {
        public int X0, X1, Y0, Y1, Pixels;
        public int W => X1 - X0 + 1;
        public int H => Y1 - Y0 + 1;
    }

    private static List<Comp> Components(bool[,] m, int w, int h)
    {
        var seen = new bool[w, h];
        var list = new List<Comp>();
        var qx = new int[w * h];
        var qy = new int[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!m[x, y] || seen[x, y]) continue;
                var head = 0;
                var tail = 0;
                qx[tail] = x;
                qy[tail] = y;
                tail++;
                seen[x, y] = true;
                var minx = x;
                var maxx = x;
                var miny = y;
                var maxy = y;
                var pix = 0;
                while (head < tail)
                {
                    var cx = qx[head];
                    var cy = qy[head];
                    head++;
                    pix++;
                    if (cx < minx) minx = cx;
                    if (cx > maxx) maxx = cx;
                    if (cy < miny) miny = cy;
                    if (cy > maxy) maxy = cy;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = cx + dx;
                            var ny = cy + dy;
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                            if (!m[nx, ny] || seen[nx, ny]) continue;
                            seen[nx, ny] = true;
                            qx[tail] = nx;
                            qy[tail] = ny;
                            tail++;
                        }
                    }
                }

                list.Add(new Comp { X0 = minx, X1 = maxx, Y0 = miny, Y1 = maxy, Pixels = pix });
            }
        }

        return list;
    }

    private static List<Comp> Glue(List<Comp> comps)
    {
        var ordered = comps.OrderBy(c => c.X0).ToList();
        var result = new List<Comp>();
        foreach (var c in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(c);
                continue;
            }

            var prev = result[^1];
            var close = c.X0 <= prev.X1 + 2;
            // Only merge side-by-side digit pieces (shared baseline), not a
            // digit stacked on the row separator under it. Heights and tops
            // must agree so ore-edge crumbs cannot glue onto a "1" stem.
            var overlapY = Math.Min(prev.Y1, c.Y1) - Math.Max(prev.Y0, c.Y0) + 1;
            var need = Math.Min(prev.H, c.H) / 2;
            var similarH = Math.Min(prev.H, c.H) * 2 >= Math.Max(prev.H, c.H);
            var alignedTop = Math.Abs(prev.Y0 - c.Y0) <= Math.Max(4, Math.Min(prev.H, c.H) / 3);
            if (close && overlapY >= need && similarH && alignedTop)
            {
                result[^1] = new Comp
                {
                    X0 = Math.Min(prev.X0, c.X0),
                    X1 = Math.Max(prev.X1, c.X1),
                    Y0 = Math.Min(prev.Y0, c.Y0),
                    Y1 = Math.Max(prev.Y1, c.Y1),
                    Pixels = prev.Pixels + c.Pixels,
                };
            }
            else result.Add(c);
        }

        return result;
    }

    private static int Classify(bool[,] m, Comp c)
    {
        var aspect = c.H / (double)Math.Max(1, c.W);
        if (c.W <= 5 && aspect >= 2.2) return 1;

        var holes = CountHoles(m, c);
        // Sparse top-only crumbs (fixture noise) are not digits.
        if (holes == 0 && c.H <= 10 && c.W >= 10)
        {
            var midY0 = (c.Y0 + c.Y1) / 2;
            var qH0 = Math.Max(1, c.H / 4);
            var mid0 = RowDensity(m, c, midY0 - qH0 / 2, midY0 + qH0 / 2);
            var top0 = RowDensity(m, c, c.Y0, c.Y0 + qH0);
            var bot0 = RowDensity(m, c, c.Y1 - qH0, c.Y1);
            if (mid0 < 0.20 && bot0 < 0.30 && top0 > 0.5) return -1;
        }
        var midX = (c.X0 + c.X1) / 2;
        var midY = (c.Y0 + c.Y1) / 2;
        var qH = Math.Max(1, c.H / 4);
        var top = RowDensity(m, c, c.Y0, c.Y0 + qH);
        var bot = RowDensity(m, c, c.Y1 - qH, c.Y1);
        var mid = RowDensity(m, c, midY - qH / 2, midY + qH / 2);
        var left = ColDensity(m, c, c.X0, c.X0 + Math.Max(1, c.W / 3));
        var right = ColDensity(m, c, c.X1 - Math.Max(1, c.W / 3), c.X1);

        // Open "0" before open-"4": hollow bowls are right-heavy and would
        // otherwise match the open-4 rule. Require near-symmetric walls so a
        // "7" top-bar does not look like a zero.
        if (holes == 0 && c.W >= 6 && top > 0.22 && bot > 0.22 && left > 0.28 && right > 0.28
            && Math.Abs(left - right) < 0.18 && Math.Abs(top - bot) < 0.28)
        {
            var midHollow = Density(m, c.X0 + c.W / 4, c.X1 - c.W / 4, midY - qH / 2, midY + qH / 2);
            if (midHollow < 0.42) return 0;
        }

        // Bare right stem with almost no left fill → open "3" (before open-4).
        if (holes == 0 && left < 0.25 && top > 0.40 && right > 0.35) return 3;

        // Open "4" is clearly right-stemmed with a mid crossbar.
        if (holes == 0 && mid > 0.22 && right > 0.4 && left < 0.35 && top < 0.55
            && right > left + 0.12) return 4;
        // Closed maple "4" can report 2 holes when the mask is sparse.
        if (holes >= 2)
        {
            if (mid > 0.18 && right > 0.4 && left < 0.5 && top < 0.55) return 4;
            return 8;
        }
        if (holes == 1)
        {
            var hr = HoleRelY(m, c);
            // Tall thin stem with an accidental hole from ore shine → "1".
            if (c.W <= 7 && aspect >= 2.15) return 1;
            // Round digit with one hole: mid bar → 8, else 0 (before right-biased 4).
            if (Math.Abs(left - right) < 0.12 && top > 0.30 && bot > 0.30)
                return mid > 0.35 ? 8 : 0;
            if (mid > 0.18 && right > 0.35 && left < 0.5 && hr <= 0.56 && (left < 0.4 || top < 0.5)) return 4;
            // Detached "4" after scroll-crown chop: right stem + crossbar, hole not low.
            if (right > 0.4 && left < 0.48 && mid > 0.14 && hr < 0.58) return 4;
            if (hr < 0.40) return 9;
            if (hr > 0.58) return 6;
            return 0;
        }

        if (aspect >= 1.9 && c.W <= 6) return 1;
        // Truncated ones-digit stem (short after crop) — still a "1".
        if (c.W <= 5 && right > 0.75 && left < 0.45) return 1;

        // Left-heavy open digit → 5 before open-6 (fixture "5", not "6").
        // A real "6" keeps a dense lower-left bowl (loL high).
        if (holes == 0 && left > 0.35 && right < 0.32 && top > 0.25 && bot > 0.28)
        {
            var loL5 = Density(m, c.X0, midX, midY, c.Y1);
            if (loL5 < 0.55) return 5;
        }

        // "2": top bar + bottom-left foot + open lower-right (before open-6).
        if (holes == 0 && top > 0.33 && left > 0.35 && right < 0.40)
        {
            var bl = Density(m, c.X0, midX, c.Y1 - qH, c.Y1);
            var loR = Density(m, midX, c.X1, midY, c.Y1);
            if (bl > 0.22 && loR < 0.50) return 2;
        }

        // Open-bottom 6 (soft outline missed the bowl seal).
        if (holes == 0 && top < 0.36 && mid > 0.35 && left > 0.28 && c.W >= 7)
        {
            var topL = Density(m, c.X0, midX, c.Y0, c.Y0 + qH);
            var topR = Density(m, midX, c.X1, c.Y0, c.Y0 + qH);
            var loL = Density(m, c.X0, midX, midY, c.Y1);
            var loR = Density(m, midX, c.X1, midY, c.Y1);
            if (topR > topL + 0.10 && loL > 0.22 && loR > 0.18) return 6;
            if (loL > 0.40 && topR < 0.35 && bot > 0.30) return 6;
        }
        // 7: dominant top bar over a weaker body (wide left fill from the bar).
        if (top > bot + 0.25 && top > 0.55 && right > 0.30 && left > 0.40) return 7;
        // A 5 has a top bar (often full width) and an open lower-right.
        if (top > 0.4 && left > 0.3)
        {
            var loR = Density(m, midX, c.X1, midY, c.Y1);
            if (loR < 0.28) return 5;
        }
        // Secondary 7: top-heavy with a right stem (not a sparse right-only crumb).
        if (top > 0.5 && bot < 0.35 && right > 0.35 && left > 0.32 && right > left + 0.05) return 7;
        // Right-heavy open digits (2/3), including a truncated bottom.
        if (top > 0.28 && right > 0.3 && left < 0.32)
        {
            var bl = Density(m, c.X0, midX, c.Y1 - qH, c.Y1);
            var br = Density(m, midX, c.X1, c.Y1 - qH, c.Y1);
            if (bot > 0.22 || br > 0.2) return bl > br + 0.1 ? 2 : 3;
        }
        if (top > 0.28 && bot > 0.28)
        {
            if (left > 0.35 && right < 0.32) return 5;
            if (right > 0.3 && left < 0.32)
            {
                var bl = Density(m, c.X0, midX, c.Y1 - qH, c.Y1);
                var br = Density(m, midX, c.X1, c.Y1 - qH, c.Y1);
                return bl > br + 0.1 ? 2 : 3;
            }

            if (left > 0.28 && right > 0.28)
            {
                var upL = Density(m, c.X0, midX, c.Y0, midY);
                var loR = Density(m, midX, c.X1, midY, c.Y1);
                if (upL > 0.22 && loR > 0.22) return 5;
            }

            return 3;
        }

        if (top > 0.35 && right > 0.3) return 7;
        return -1;
    }

    private static int CountHoles(bool[,] m, Comp c)
    {
        var w = c.W + 2;
        var h = c.H + 2;
        var cell = new byte[w, h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) cell[x, y] = 0;
                else cell[x, y] = m[c.X0 + x - 1, c.Y0 + y - 1] ? (byte)1 : (byte)0;
            }
        }

        var qx = new int[w * h];
        var qy = new int[w * h];
        var head = 0;
        var tail = 0;
        qx[tail] = 0;
        qy[tail] = 0;
        tail++;
        cell[0, 0] = 2;
        while (head < tail)
        {
            var cx = qx[head];
            var cy = qy[head];
            head++;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) != 1) continue;
                    var nx = cx + dx;
                    var ny = cy + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    if (cell[nx, ny] != 0) continue;
                    cell[nx, ny] = 2;
                    qx[tail] = nx;
                    qy[tail] = ny;
                    tail++;
                }
            }
        }

        var holes = 0;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                if (cell[x, y] != 0) continue;
                holes++;
                head = 0;
                tail = 0;
                qx[tail] = x;
                qy[tail] = y;
                tail++;
                cell[x, y] = 2;
                while (head < tail)
                {
                    var cx = qx[head];
                    var cy = qy[head];
                    head++;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (Math.Abs(dx) + Math.Abs(dy) != 1) continue;
                            var nx = cx + dx;
                            var ny = cy + dy;
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                            if (cell[nx, ny] != 0) continue;
                            cell[nx, ny] = 2;
                            qx[tail] = nx;
                            qy[tail] = ny;
                            tail++;
                        }
                    }
                }
            }
        }

        return holes;
    }

    private static double HoleRelY(bool[,] m, Comp c)
    {
        var w = c.W + 2;
        var h = c.H + 2;
        var cell = new byte[w, h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) cell[x, y] = 0;
                else cell[x, y] = m[c.X0 + x - 1, c.Y0 + y - 1] ? (byte)1 : (byte)0;
            }
        }

        var qx = new int[w * h];
        var qy = new int[w * h];
        var head = 0;
        var tail = 0;
        qx[tail] = 0;
        qy[tail] = 0;
        tail++;
        cell[0, 0] = 2;
        while (head < tail)
        {
            var cx = qx[head];
            var cy = qy[head];
            head++;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) != 1) continue;
                    var nx = cx + dx;
                    var ny = cy + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    if (cell[nx, ny] != 0) continue;
                    cell[nx, ny] = 2;
                    qx[tail] = nx;
                    qy[tail] = ny;
                    tail++;
                }
            }
        }

        long sum = 0;
        var n = 0;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                if (cell[x, y] != 0) continue;
                head = 0;
                tail = 0;
                qx[tail] = x;
                qy[tail] = y;
                tail++;
                cell[x, y] = 3;
                while (head < tail)
                {
                    var cx = qx[head];
                    var cy = qy[head];
                    head++;
                    sum += cy;
                    n++;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (Math.Abs(dx) + Math.Abs(dy) != 1) continue;
                            var nx = cx + dx;
                            var ny = cy + dy;
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                            if (cell[nx, ny] != 0) continue;
                            cell[nx, ny] = 3;
                            qx[tail] = nx;
                            qy[tail] = ny;
                            tail++;
                        }
                    }
                }

                return n == 0 ? 0.5 : (sum / (double)n - 1) / Math.Max(1, c.H);
            }
        }

        return 0.5;
    }

    private static double RowDensity(bool[,] m, Comp c, int y0, int y1)
    {
        y0 = Math.Max(c.Y0, y0);
        y1 = Math.Min(c.Y1, y1);
        if (y1 < y0) return 0;
        var on = 0;
        var tot = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = c.X0; x <= c.X1; x++)
            {
                tot++;
                if (m[x, y]) on++;
            }
        }

        return on / (double)tot;
    }

    private static double ColDensity(bool[,] m, Comp c, int x0, int x1)
    {
        x0 = Math.Max(c.X0, x0);
        x1 = Math.Min(c.X1, x1);
        if (x1 < x0) return 0;
        var on = 0;
        var tot = 0;
        for (var y = c.Y0; y <= c.Y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                tot++;
                if (m[x, y]) on++;
            }
        }

        return on / (double)tot;
    }

    private static double Density(bool[,] m, int x0, int x1, int y0, int y1)
    {
        if (x1 < x0 || y1 < y0) return 0;
        var on = 0;
        var tot = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                tot++;
                if (m[x, y]) on++;
            }
        }

        return on / (double)tot;
    }
}
