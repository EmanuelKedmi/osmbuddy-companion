namespace MarketNotes.Companion.Scanning;

public enum BattleButton
{
    None,
    /// <summary>Orange START. Analysis is idle.</summary>
    Start,
    /// <summary>Green CANCEL. Analysis is running.</summary>
    Cancel,
}

public readonly record struct BattleHit(PixelRect Window, BattleButton Button);

/// <summary>
/// Held is a missed frame that is still using the last good rectangle.
/// Lost is true only after that hold expires, which is a confident absence.
/// </summary>
public readonly record struct BattleSight(PixelRect? Window, BattleButton Button, bool Held, bool Lost);

/// <summary>
/// The battle analysis panel is translucent, so its fill is not a color
/// we can match. The opaque title strip ("Battle Analysis") is the anchor:
/// a light bar with dark lettering. A START or CANCEL control then has to
/// sit at that strip's bottom-right. A lime or orange blob with no title
/// is not the window, which is how the chat corner used to win.
/// </summary>
public static class BattleWindowFinder
{
    public static PixelRect? Find(byte[] bgra, int width, int height, int stride) =>
        FindHit(bgra, width, height, stride)?.Window;

    public static BattleHit? FindHit(byte[] bgra, int width, int height, int stride)
    {
        if (width < 40 || height < 40 || stride < width * 4 || bgra.Length < stride * height)
        {
            return null;
        }

        BattleHit? best = null;
        var bestArea = 0;
        foreach (var button in FindButtons(bgra, width, height, stride))
        {
            var window = TitleWindow(bgra, width, height, stride, button);
            if (window == null)
            {
                continue;
            }

            var area = window.Value.Width * window.Value.Height;
            if (area <= bestArea)
            {
                continue;
            }

            bestArea = area;
            best = new BattleHit(window.Value, button.Kind);
        }

        return best;
    }

    /// <summary>
    /// Text from the crop is the battle panel when it names the window or one
    /// of its rows. Chat ("TRADE") and loot lines are not.
    /// </summary>
    public static bool LooksLikePanel(IReadOnlyList<string> lines)
    {
        var text = string.Join(' ', lines).ToLowerInvariant();
        if (text.Contains("battle", StringComparison.Ordinal)
            || text.Contains("elapsed", StringComparison.Ordinal)
            || text.Contains("dmg", StringComparison.Ordinal)
            || text.Contains("mob", StringComparison.Ordinal)
            || text.Contains("exp", StringComparison.Ordinal)
            || text.Contains("meso", StringComparison.Ordinal)
            || text.Contains("cancel", StringComparison.Ordinal)
            || text.Contains("start", StringComparison.Ordinal)
            || text.Contains("/m", StringComparison.Ordinal)
            || text.Contains("/s", StringComparison.Ordinal))
        {
            return true;
        }

        for (var i = 0; i + 3 < text.Length; i++)
        {
            if (char.IsDigit(text[i]) && text[i + 1] == ':' && char.IsDigit(text[i + 2]) && char.IsDigit(text[i + 3]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keeps the last confident panel for a few frames when the button
    /// blinks out, and ignores a single far-away blob so the guide does
    /// not jump. A frame that never had a button still returns nothing,
    /// which is not the middle of the map.
    /// </summary>
    public sealed class Track
    {
        public const int HoldFrames = 4;

        private PixelRect? _window;
        private PixelRect? _candidate;
        private PixelRect? _dismissed;
        private BattleButton _button;
        private bool _lost;
        private int _misses;
        private int _dismissLeft;
        private int _width;
        private int _height;

        public void Reset()
        {
            _window = null;
            _candidate = null;
            _dismissed = null;
            _button = BattleButton.None;
            _lost = false;
            _misses = 0;
            _dismissLeft = 0;
            _width = 0;
            _height = 0;
        }

        /// <summary>
        /// Drops a rectangle whose read was not the battle panel, and ignores
        /// that same place for a few frames so the guide does not stick there.
        /// </summary>
        public void Dismiss(PixelRect window)
        {
            _dismissed = window;
            _dismissLeft = 12;
            if (_window is { } current && (Overlaps(current, window) || SamePlace(current, window)))
            {
                _window = null;
                _candidate = null;
                _button = BattleButton.None;
                _misses = 0;
            }
        }

        public PixelRect? Observe(byte[] bgra, int width, int height, int stride) =>
            ObserveSight(bgra, width, height, stride).Window;

        public BattleSight ObserveSight(byte[] bgra, int width, int height, int stride)
        {
            if (width != _width || height != _height)
            {
                _window = null;
                _candidate = null;
                _button = BattleButton.None;
                _lost = false;
                _misses = 0;
                _width = width;
                _height = height;
            }

            var found = FindHit(bgra, width, height, stride);
            if (found is { } bannedHit
                && _dismissLeft > 0
                && _dismissed is { } banned
                && (Overlaps(banned, bannedHit.Window) || SamePlace(banned, bannedHit.Window)))
            {
                _dismissLeft--;
                found = null;
            }
            else if (_dismissLeft > 0)
            {
                _dismissLeft--;
            }

            if (found is { } next)
            {
                _lost = false;
                if (_window is { } prior && !Overlaps(prior, next.Window) && !SamePlace(prior, next.Window))
                {
                    if (_candidate is { } pending && (Overlaps(pending, next.Window) || SamePlace(pending, next.Window)))
                    {
                        _window = next.Window;
                        _button = next.Button;
                        _candidate = null;
                        _misses = 0;
                        return new BattleSight(next.Window, next.Button, false, false);
                    }

                    _candidate = next.Window;
                    return new BattleSight(prior, _button, false, false);
                }

                _candidate = null;
                _misses = 0;
                _button = next.Button;
                if (_window is { } kept && SamePlace(kept, next.Window))
                {
                    return new BattleSight(kept, next.Button, false, false);
                }

                _window = next.Window;
                return new BattleSight(next.Window, next.Button, false, false);
            }

            _candidate = null;
            if (_window == null)
            {
                return new BattleSight(null, BattleButton.None, false, _lost);
            }

            _misses++;
            if (_misses > HoldFrames)
            {
                _window = null;
                _button = BattleButton.None;
                _lost = true;
                return new BattleSight(null, BattleButton.None, false, true);
            }

            return new BattleSight(_window, _button, true, false);
        }

        private static bool Overlaps(PixelRect a, PixelRect b)
        {
            var right = Math.Min(a.X + a.Width, b.X + b.Width);
            var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return right > Math.Max(a.X, b.X) && bottom > Math.Max(a.Y, b.Y);
        }

        private static bool SamePlace(PixelRect a, PixelRect b)
        {
            var slack = Math.Max(8, Math.Max(a.Width, b.Width) / 20);
            return Math.Abs(a.X - b.X) <= slack
                && Math.Abs(a.Y - b.Y) <= slack
                && Math.Abs(a.Width - b.Width) <= slack
                && Math.Abs(a.Height - b.Height) <= slack;
        }
    }

    private readonly record struct ButtonBox(int X, int Y, int Right, int Bottom, BattleButton Kind);

    private static List<ButtonBox> FindButtons(byte[] bgra, int width, int height, int stride)
    {
        var minWidth = Math.Max(12, width / 120);
        var maxWidth = Math.Max(minWidth + 8, width / 4);
        var maxHeight = Math.Max(18, height / 8);
        var minRun = Math.Max(3, minWidth / 5);
        var join = Math.Max(2, height / 100);
        var runs = new List<(int Y, int X0, int X1)>();
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var run = -1;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                if (IsAnchor(bgra[i + 2], bgra[i + 1], bgra[i]))
                {
                    if (run < 0)
                    {
                        run = x;
                    }
                }
                else if (run >= 0)
                {
                    KeepRun(y, run, x - 1);
                    run = -1;
                }
            }

            if (run >= 0)
            {
                KeepRun(y, run, width - 1);
            }
        }

        var found = new List<ButtonBox>();
        var used = new bool[runs.Count];
        for (var i = 0; i < runs.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            var x0 = runs[i].X0;
            var x1 = runs[i].X1;
            var y0 = runs[i].Y;
            var y1 = runs[i].Y;
            var pixels = runs[i].X1 - runs[i].X0 + 1;
            used[i] = true;
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var j = 0; j < runs.Count; j++)
                {
                    if (used[j] || runs[j].Y < y0 - join || runs[j].Y > y1 + join)
                    {
                        continue;
                    }

                    var overlap = Math.Min(x1, runs[j].X1) - Math.Max(x0, runs[j].X0) + 1;
                    if (overlap * 2 < Math.Min(x1 - x0 + 1, runs[j].X1 - runs[j].X0 + 1))
                    {
                        continue;
                    }

                    used[j] = true;
                    x0 = Math.Min(x0, runs[j].X0);
                    x1 = Math.Max(x1, runs[j].X1);
                    y0 = Math.Min(y0, runs[j].Y);
                    y1 = Math.Max(y1, runs[j].Y);
                    pixels += runs[j].X1 - runs[j].X0 + 1;
                    grew = true;
                }
            }

            var blobWidth = x1 - x0 + 1;
            var blobHeight = y1 - y0 + 1;
            if (blobWidth < minWidth || blobWidth > maxWidth || blobHeight < 6 || blobHeight > maxHeight)
            {
                continue;
            }

            if (blobWidth < blobHeight * 2 || blobWidth > blobHeight * 8)
            {
                continue;
            }

            var area = blobWidth * blobHeight;
            if (pixels * 5 < area * 2)
            {
                continue;
            }

            var box = new ButtonBox(x0, y0, x1, y1, BattleButton.None);
            found.Add(box with { Kind = KindOf(bgra, stride, box) });
        }

        return found;

        void KeepRun(int y, int x0, int x1)
        {
            var length = x1 - x0 + 1;
            if (length >= minRun && length <= maxWidth)
            {
                runs.Add((y, x0, x1));
            }
        }
    }

    /// <summary>
    /// The title bar is the opaque light strip above the button. The rest
    /// of the panel shows the map through it, so only this strip marks
    /// the top and side edges.
    /// </summary>
    private static PixelRect? TitleWindow(byte[] bgra, int width, int height, int stride, ButtonBox button)
    {
        var buttonWidth = button.Right - button.X + 1;
        var buttonHeight = Math.Max(1, button.Bottom - button.Y + 1);
        var needed = Math.Max(buttonWidth * 3 / 2, 24);
        var reach = Math.Max(buttonHeight * 14, buttonWidth * 5);
        var yStop = Math.Max(0, button.Y - reach);
        var xFrom = Math.Max(0, button.X - buttonWidth * 5);
        var xTo = Math.Min(width - 1, button.Right + buttonWidth / 2);
        var inward = Math.Max(8, buttonWidth / 5);
        var outward = Math.Max(8, buttonWidth / 3);
        var rightMin = button.Right - inward;
        var rightMax = Math.Min(width - 1, button.Right + outward);
        var hits = new List<(int Y, int X0, int X1)>();
        for (var y = button.Y - 2; y >= yStop; y--)
        {
            var run = BestTitleRun(bgra, stride, y, xFrom, xTo, needed, button.X, rightMin, rightMax);
            if (run != null)
            {
                hits.Add((y, run.Value.X0, run.Value.X1));
            }
        }

        if (hits.Count == 0)
        {
            return null;
        }

        var gap = Math.Max(buttonHeight, buttonWidth / 3);
        var minRows = Math.Max(3, buttonHeight / 3);
        List<(int Y, int X0, int X1)>? band = null;
        var current = new List<(int Y, int X0, int X1)>();
        foreach (var hit in hits)
        {
            if (current.Count > 0 && current[^1].Y - hit.Y > gap)
            {
                if (current.Count >= minRows)
                {
                    band = current;
                    break;
                }

                current = [];
            }

            current.Add(hit);
        }

        if (band == null && current.Count >= minRows)
        {
            band = current;
        }

        if (band == null)
        {
            return null;
        }

        var top = band.Min(row => row.Y);
        var bottomRow = band.Max(row => row.Y);
        var maxBand = Math.Max(buttonHeight * 3, buttonWidth);
        if (bottomRow - top > maxBand)
        {
            top = bottomRow - maxBand;
        }

        var left = band.Where(row => row.Y >= top).Min(row => row.X0);
        var rightEdge = band.Where(row => row.Y >= top).Max(row => row.X1);
        var padX = Math.Max(2, buttonWidth / 12);
        var padY = Math.Max(2, buttonHeight / 6);
        var windowLeft = Math.Max(0, left - padX);
        var windowTop = Math.Max(0, top - padY);
        var right = Math.Min(width, Math.Max(rightEdge, button.Right) + 1 + padX);
        var bottom = Math.Min(height, button.Bottom + 1 + padY);
        var winWidth = right - windowLeft;
        var winHeight = bottom - windowTop;
        if (winWidth < buttonWidth * 2 || winHeight < buttonHeight * 4)
        {
            return null;
        }

        if (winWidth > buttonWidth * 8 || winHeight > buttonHeight * 16)
        {
            return null;
        }

        if (CountInk(bgra, stride, top, bottomRow, left, rightEdge) < Math.Max(4, (rightEdge - left) / 30))
        {
            return null;
        }

        return new PixelRect(windowLeft, windowTop, winWidth, winHeight);
    }

    private static int CountInk(byte[] bgra, int stride, int top, int bottom, int left, int right)
    {
        var ink = 0;
        for (var y = top; y <= bottom; y++)
        {
            var row = y * stride;
            for (var x = left; x <= right; x++)
            {
                var i = row + x * 4;
                if ((uint)i + 2 >= (uint)bgra.Length) continue;
                if (IsInk(bgra[i + 2], bgra[i + 1], bgra[i])) ink++;
            }
        }

        return ink;
    }

    private static (int X0, int X1)? BestTitleRun(
        byte[] bgra,
        int stride,
        int y,
        int xFrom,
        int xTo,
        int needed,
        int buttonX,
        int rightMin,
        int rightMax)
    {
        (int X0, int X1)? best = null;
        var run = -1;
        var gap = 0;
        var gapMax = Math.Max(4, needed / 8);
        var row = y * stride;
        for (var x = xFrom; x <= xTo; x++)
        {
            var i = row + x * 4;
            var r = bgra[i + 2];
            var g = bgra[i + 1];
            var b = bgra[i];
            if (IsTitle(r, g, b))
            {
                gap = 0;
                if (run < 0)
                {
                    run = x;
                }
            }
            else if (run >= 0 && gap < gapMax && IsInk(r, g, b))
            {
                gap++;
            }
            else if (run >= 0)
            {
                Consider(run, x - 1);
                run = -1;
                gap = 0;
            }
        }

        if (run >= 0)
        {
            Consider(run, xTo);
        }

        return best;

        void Consider(int x0, int x1)
        {
            var length = x1 - x0 + 1;
            if (length < needed || x0 >= buttonX || x1 < rightMin || x1 > rightMax)
            {
                return;
            }

            if (best == null || length > best.Value.X1 - best.Value.X0)
            {
                best = (x0, x1);
            }
        }
    }

    /// <summary>
    /// START is orange: red high, green mid, blue low. The live fill used
    /// by the tests sits near (230, 140, 40).
    /// CANCEL is the same control in lime. On the live panel the solid
    /// rows run from about (153, 187, 17) to (221, 221, 68). Leaf green is
    /// darker in red and higher in blue, so it does not match.
    /// </summary>
    private static BattleButton KindOf(byte[] bgra, int stride, ButtonBox box)
    {
        var orange = 0;
        var green = 0;
        for (var y = box.Y; y <= box.Bottom; y++)
        {
            var row = y * stride;
            for (var x = box.X; x <= box.Right; x++)
            {
                var i = row + x * 4;
                if ((uint)i + 2 >= (uint)bgra.Length) continue;
                if (IsOrange(bgra[i + 2], bgra[i + 1], bgra[i])) orange++;
                else if (IsGreen(bgra[i + 2], bgra[i + 1], bgra[i])) green++;
            }
        }

        if (green > orange && green >= 4) return BattleButton.Cancel;
        if (orange > green && orange >= 4) return BattleButton.Start;
        return BattleButton.None;
    }

    private static bool IsAnchor(byte r, byte g, byte b) => IsOrange(r, g, b) || IsGreen(r, g, b);

    private static bool IsOrange(byte r, byte g, byte b) =>
        r >= 175 && g is >= 80 and <= 190 && b <= 110 && r >= g + 35 && r >= b + 70;

    private static bool IsGreen(byte r, byte g, byte b)
    {
        if (g < 145 || r < 125 || b > 90)
        {
            return false;
        }

        if (g - b < 100 || r - b < 80)
        {
            return false;
        }

        if (Math.Abs(r - g) > 50 || g > r + 45)
        {
            return false;
        }

        return true;
    }

    /// <summary>Dark title lettering. A blank light bar is not the window name.</summary>
    private static bool IsInk(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        return max < 110;
    }

    /// <summary>
    /// Opaque title strip: light gray through near-white, almost no chroma.
    /// The live bar is often brighter than 230, which the old window missed.
    /// </summary>
    private static bool IsTitle(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max - min > 28)
        {
            return false;
        }

        return (r + g + b) / 3 >= 185;
    }
}
