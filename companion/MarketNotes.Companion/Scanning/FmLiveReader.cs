using System.Text.RegularExpressions;
using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Modes;

namespace MarketNotes.Companion.Scanning;

public sealed class FmLiveView
{
    public required string Detail { get; init; }
    public required IReadOnlyList<FoundRegion> Regions { get; init; }
    public FmFrameInput? Shop { get; init; }
    public bool ShopFrameFound { get; init; }
    /// <summary>
    /// True when owner, shop name, or priced rows changed since the last
    /// frame that carried a shop. Used to skip repeat website uploads, not to
    /// hide rows from the companion table.
    /// </summary>
    public bool ListingsChanged { get; init; }
    public int LeftOut { get; init; }
    public IReadOnlyList<FmVisibleRow> Hidden { get; init; } = [];
}

public readonly record struct FoundRegion(string Name, int X, int Y, int Width, int Height);

/// <summary>
/// Locates the minimap, shop frame, and scrollbar, then reads those rectangles.
/// </summary>
public sealed class FmLiveReader
{
    private static readonly Regex ChannelText = new(
        @"\b(?:ch|channel)\s*\.?\s*(\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private string? _mapHash;
    private string? _titleHash;
    private string? _rowHash;
    private string? _listed;
    private string? _shopDumpHash;
    private int _priced;
    private IReadOnlyList<string> _map = [];
    private IReadOnlyList<string> _title = [];
    private IReadOnlyList<string> _rows = [];
    private IReadOnlyList<FmVisibleRow> _hidden = [];
    private UiChrome _chrome = new() { Note = "No frame yet." };

    // Shop-row OCR is slow (~0.5–2s). Run it off the share tick and keep only
    // the latest crop so scrolling does not queue a backlog of stale pages.
    private readonly object _rowOcrGate = new();
    private int _rowOcrBusy;
    private byte[]? _pendingRowBgra;
    private int _pendingRowWidth;
    private int _pendingRowHeight;
    private string? _pendingRowHash;
    private IReadOnlyList<string>? _readyRows;
    private string? _readyRowHash;
    private bool _rowsJustUpdated;

    public void Reset()
    {
        lock (_rowOcrGate)
        {
            _pendingRowBgra = null;
            _pendingRowHash = null;
            _readyRows = null;
            _readyRowHash = null;
            _rowsJustUpdated = false;
        }

        _mapHash = _titleHash = _rowHash = _listed = _shopDumpHash = null;
        _priced = 0;
        _map = _title = _rows = [];
        _hidden = [];
        _chrome = new UiChrome { Note = "No frame yet." };
    }

    /// <summary>
    /// Forgets the last uploaded listing set so the next open shop is treated
    /// as new, without dropping cached OCR crops.
    /// </summary>
    public void ForgetListings()
    {
        _listed = null;
        _priced = 0;
    }

    public FmLiveView Read(CapturedFrame frame)
    {
        var stride = frame.Height > 0 && frame.Bgra.Length >= frame.Height ? frame.Bgra.Length / frame.Height : 0;
        _chrome = UiChromeFinder.Find(frame.Bgra, frame.Width, frame.Height, stride);
        if (!WindowsOcr.IsAvailable)
        {
            return View(WindowsOcr.Status + " " + _chrome.Note, null, _chrome.Shop is { IsEmpty: false });
        }

        _map = ReadMap(frame, _chrome.MapTitle, ref _mapHash, _map);
        _title = ReadShopTitle(frame, _chrome.ShopTitle, ref _titleHash, _title);
        // Item inventory uses the same pale frame as a hired merchant. Prefer
        // another pale window when the title is clearly the bag.
        var ignored = new List<PixelRect>();
        while (LooksLikeInventory(_title) && _chrome.Shop is PixelRect inventory && ignored.Count < 3)
        {
            ignored.Add(inventory);
            _titleHash = null;
            _rowHash = null;
            _rows = [];
            ClearPendingRowOcr();
            _chrome = UiChromeFinder.Find(
                frame.Bgra,
                frame.Width,
                frame.Height,
                frame.Height > 0 && frame.Bgra.Length >= frame.Height ? frame.Bgra.Length / frame.Height : 0,
                ignored);
            _title = ReadShopTitle(frame, _chrome.ShopTitle, ref _titleHash, []);
        }

        if (LooksLikeInventory(_title))
        {
            // Bag is the only pale window. Do not treat it as a shop.
            _chrome = new UiChrome
            {
                Minimap = _chrome.Minimap,
                MapTitle = _chrome.MapTitle,
                Note = _chrome.Note + " Ignored item inventory (not a hired merchant).",
            };
            _title = [];
            _rows = [];
            _titleHash = _rowHash = null;
            ClearPendingRowOcr();
        }

        // Prefer a pale window whose title OCR names a hired merchant / store.
        if (_chrome.Shop is PixelRect firstShop
            && !LooksLikeMerchant(_title)
            && ignored.Count < 3)
        {
            var skip = new List<PixelRect>(ignored) { firstShop };
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var alt = UiChromeFinder.Find(
                    frame.Bgra,
                    frame.Width,
                    frame.Height,
                    frame.Height > 0 && frame.Bgra.Length >= frame.Height ? frame.Bgra.Length / frame.Height : 0,
                    skip);
                if (alt.Shop is not PixelRect next) break;
                _titleHash = null;
                var altTitle = ReadShopTitle(frame, alt.ShopTitle, ref _titleHash, []);
                if (LooksLikeMerchant(altTitle) && !LooksLikeInventory(altTitle))
                {
                    _chrome = alt;
                    _title = altTitle;
                    _rowHash = null;
                    _rows = [];
                    ClearPendingRowOcr();
                    break;
                }

                skip.Add(next);
            }
        }

        var rowsChanged = TakeReadyRows();
        // Full-shop PNG dumps are large and used to run every share tick in
        // DevBuild, which made the UI miss scroll pages. Only dump when the
        // shop pixels actually change.
        if (_chrome.Shop is { IsEmpty: false } shopBounds && DebugLog.Enabled)
        {
            var whole = RegionCrop.CropRect(frame, shopBounds.X, shopBounds.Y, shopBounds.Width, shopBounds.Height);
            if (whole.Ok)
            {
                var shopHash = DropRegion.Hash(whole.Bgra);
                if (shopHash != _shopDumpHash)
                {
                    _shopDumpHash = shopHash;
                    InkSampleDump.Save(
                        "shop",
                        whole.Bgra,
                        whole.Width,
                        whole.Height,
                        _chrome.Note + " title=" + (_title.Count == 0 ? "(empty)" : string.Join(" | ", _title)));
                }
            }
        }

        if (_chrome.ShopRows is PixelRect rows)
        {
            var crop = RegionCrop.CropRect(frame, rows.X, rows.Y, rows.Width, rows.Height);
            if (crop.Ok)
            {
                var hash = DropRegion.Hash(crop.Bgra);
                if (hash != _rowHash) QueueRowOcr(crop.Bgra, crop.Width, crop.Height, hash, rows);
            }
        }

        var mapName = MapName(_map);
        var channel = Channel(_map, _title);
        var parsed = _rows.Count == 0 || _chrome.Shop == null
            ? null
            : ShopListingParser.Parse(mapName, channel, _chrome.ScrollPosition, _title, _rows);
        // Quantity stays at the parser default (1). Icon digit reading was too
        // brittle and did not buy scan speed — leave it off for now.
        if (parsed != null && _chrome.ShopRows is PixelRect toneBox)
        {
            var tone = RegionCrop.CropRect(frame, toneBox.X, toneBox.Y, toneBox.Width, toneBox.Height);
            if (tone.Ok)
                ShopRowTone.Mark(tone.Bgra, tone.Width, tone.Height, tone.Width * 4, parsed.Rows);
        }
        if (parsed != null)
        {
            parsed.Rows = ShopListingParser.WithCatalogNames(parsed.Rows, out var hidden);
            parsed.Unreadable += hidden.Count;
            ShopListingParser.HoldSalesWhileCovered(parsed.Rows, hidden);
            _hidden = hidden;
        }
        else
        {
            _hidden = [];
        }
        // Always hand the current priced rows to the UI. Gating on an item-only
        // signature used to skip Absorb/ShowLocalShop while status still said
        // "Listings this frame: N", so the FM table stayed empty or stale.
        FmFrameInput? shop = null;
        var listingsChanged = false;
        var leftOut = parsed?.Unreadable ?? 0;
        if (parsed != null && parsed.Rows.Count > 0)
        {
            _priced = parsed.Rows.Count;
            shop = parsed;
            var signature = FmListingSignature.From(parsed);
            if (signature != _listed)
            {
                _listed = signature;
                listingsChanged = true;
            }
        }
        else if (rowsChanged)
        {
            _priced = 0;
            _listed = null;
            listingsChanged = true;
        }

        var priced = _priced == 0
            ? "Listings this frame: 0."
            : "Listings this frame: " + _priced + ". " + string.Join("; ", (parsed?.Rows ?? []).Select(row => $"{row.Item} x{row.Quantity} {row.PriceEach.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}"));
        if (leftOut > 0)
        {
            priced += " Left out " + leftOut + (leftOut == 1 ? " unreadable row." : " unreadable rows.");
        }

        if (Volatile.Read(ref _rowOcrBusy) != 0)
            priced = "Reading shop rows… " + priced;
        var detail = string.Join("\n",
        [
            WindowsOcr.Status,
            _chrome.Note,
            mapName.Length == 0 ? "Map name: nothing read from the minimap title." : "Map name: " + mapName,
            "Shop title: " + (_title.Count == 0 ? "nothing read." : string.Join(" | ", _title)),
            "Shop rows: " + (_rows.Count == 0 ? "nothing read." : string.Join(" | ", _rows)),
            priced,
        ]);
        return View(detail, shop, _chrome.Shop is { IsEmpty: false }, listingsChanged, leftOut);
    }

    private FmLiveView View(
        string detail,
        FmFrameInput? shop,
        bool shopFrameFound = false,
        bool listingsChanged = false,
        int leftOut = 0)
    {
        var regions = new List<FoundRegion>();
        Add(regions, "Minimap", _chrome.Minimap);
        Add(regions, "Shop frame", _chrome.Shop);
        Add(regions, "Scrollbar", _chrome.Scrollbar);
        Add(regions, "Shop rows", _chrome.ShopRows);
        return new FmLiveView
        {
            Detail = detail,
            Regions = regions,
            Shop = shop,
            ShopFrameFound = shopFrameFound,
            ListingsChanged = listingsChanged,
            LeftOut = leftOut,
            Hidden = _hidden,
        };
    }

    private static void Add(List<FoundRegion> regions, string name, PixelRect? rect)
    {
        if (rect is not PixelRect found || found.IsEmpty) return;
        regions.Add(new FoundRegion(name, found.X, found.Y, found.Width, found.Height));
    }

    private readonly record struct PaddedImage(byte[] Pixels, int Width, int Height);

    /// <summary>
    /// Windows OCR drops text that sits on the edge of the bitmap. A margin
    /// keeps the first item rows in the read.
    /// </summary>
    private static PaddedImage PadForOcr(byte[] bgra, int width, int height, int margin)
    {
        var nextWidth = width + margin * 2;
        var nextHeight = height + margin * 2;
        var pixels = new byte[nextWidth * nextHeight * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 244;
            pixels[i + 1] = 240;
            pixels[i + 2] = 232;
            pixels[i + 3] = 255;
        }

        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(bgra, y * width * 4, pixels, ((y + margin) * nextWidth + margin) * 4, width * 4);
        }

        return new PaddedImage(pixels, nextWidth, nextHeight);
    }

    private static IReadOnlyList<string> ReadMap(CapturedFrame frame, PixelRect? rect, ref string? hash, IReadOnlyList<string> previous)
    {
        if (rect is not PixelRect box) return previous;
        var crop = RegionCrop.CropRect(frame, box.X, box.Y, box.Width, box.Height);
        if (!crop.Ok) return previous;
        var next = DropRegion.Hash(crop.Bgra);
        if (next == hash) return previous;
        hash = next;
        return MapTitleRead.Lines(crop.Bgra, crop.Width, crop.Height);
    }

    private static IReadOnlyList<string> ReadCrop(CapturedFrame frame, PixelRect? rect, ref string? hash, IReadOnlyList<string> previous)
    {
        if (rect is not PixelRect box) return previous;
        var crop = RegionCrop.CropRect(frame, box.X, box.Y, box.Width, box.Height);
        if (!crop.Ok) return previous;
        var next = DropRegion.Hash(crop.Bgra);
        if (next == hash) return previous;
        hash = next;
        return WindowsOcr.ReadLines(crop.Bgra, crop.Width, crop.Height);
    }

    private static bool LooksLikeInventory(IReadOnlyList<string> titleLines)
    {
        var text = string.Join(" ", titleLines).ToLowerInvariant();
        if (text.Length == 0) return false;
        text = Regex.Replace(text, @"[^a-z0-9]+", " ");
        return text.Contains("item inventory", StringComparison.Ordinal)
            || text.Contains("equipment inventory", StringComparison.Ordinal)
            || text.Contains("etc inventory", StringComparison.Ordinal)
            || text.Contains("install inventory", StringComparison.Ordinal)
            || text.Contains("cash inventory", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"\bitem\b") && text.Contains("invent", StringComparison.Ordinal)
            || text is "item" or "items" or "equip" or "equipment" or "etc" or "cash";
    }

    private static bool LooksLikeMerchant(IReadOnlyList<string> titleLines)
    {
        var text = string.Join(" ", titleLines).ToLowerInvariant();
        if (text.Length == 0) return false;
        text = Regex.Replace(text, @"[^a-z0-9]+", " ");
        return text.Contains("hired merchant", StringComparison.Ordinal)
            || text.Contains("merchant", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"\bstore\b");
    }

    private static IReadOnlyList<string> ReadShopTitle(CapturedFrame frame, PixelRect? rect, ref string? hash, IReadOnlyList<string> previous)
    {
        if (rect is not PixelRect box) return previous;
        var crop = RegionCrop.CropRect(frame, box.X, box.Y, box.Width, box.Height);
        if (!crop.Ok) return previous;
        var next = DropRegion.Hash(crop.Bgra);
        if (next == hash) return previous;
        hash = next;
        var lines = WindowsOcr.ReadLines(crop.Bgra, crop.Width, crop.Height);
        if (lines.Count == 0)
        {
            lines = WindowsOcr.ReadLines(
                GameFrame.PointScale(crop.Bgra, crop.Width, crop.Height, crop.Width * 2, crop.Height * 2),
                crop.Width * 2,
                crop.Height * 2);
        }

        if (DebugLog.Enabled)
        {
            InkSampleDump.Save(
                "shop-title",
                crop.Bgra,
                crop.Width,
                crop.Height,
                lines.Count == 0 ? "(empty)" : string.Join(" | ", lines));
        }

        return lines;
    }

    private void ClearPendingRowOcr()
    {
        lock (_rowOcrGate)
        {
            _pendingRowBgra = null;
            _pendingRowHash = null;
            _readyRows = null;
            _readyRowHash = null;
            _rowsJustUpdated = false;
        }
    }

    private bool TakeReadyRows()
    {
        IReadOnlyList<string>? ready = null;
        string? hash = null;
        lock (_rowOcrGate)
        {
            if (!_rowsJustUpdated) return false;
            ready = _readyRows;
            hash = _readyRowHash;
            _readyRows = null;
            _readyRowHash = null;
            _rowsJustUpdated = false;
        }

        if (ready == null || hash == null) return false;
        _rows = ready;
        _rowHash = hash;
        return true;
    }

    private void QueueRowOcr(byte[] bgra, int width, int height, string hash, PixelRect rows)
    {
        // Already showing this page, or already queued / running it.
        if (hash == _rowHash) return;
        lock (_rowOcrGate)
        {
            if (hash == _pendingRowHash || hash == _readyRowHash) return;
            _pendingRowBgra = (byte[])bgra.Clone();
            _pendingRowWidth = width;
            _pendingRowHeight = height;
            _pendingRowHash = hash;
        }

        if (Interlocked.CompareExchange(ref _rowOcrBusy, 1, 0) != 0) return;
        var dumpRect = rows;
        _ = Task.Run(() => RowOcrWorker(dumpRect));
    }

    private void RowOcrWorker(PixelRect dumpRect)
    {
        try
        {
            while (true)
            {
                byte[]? bgra;
                int width, height;
                string? hash;
                lock (_rowOcrGate)
                {
                    bgra = _pendingRowBgra;
                    width = _pendingRowWidth;
                    height = _pendingRowHeight;
                    hash = _pendingRowHash;
                    _pendingRowBgra = null;
                    _pendingRowHash = null;
                }

                if (bgra == null || hash == null) break;

                var lines = ReadShopRows(bgra, width, height);
                lock (_rowOcrGate)
                {
                    // A newer crop arrived while we were reading — discard this
                    // result and loop onto the pending page.
                    if (_pendingRowHash != null) continue;
                    _readyRows = lines;
                    _readyRowHash = hash;
                    _rowsJustUpdated = true;
                }

                if (DebugLog.Enabled)
                {
                    InkSampleDump.Save(
                        "shop-rows",
                        bgra,
                        width,
                        height,
                        $"rect={dumpRect.X},{dumpRect.Y} {dumpRect.Width}x{dumpRect.Height}; " +
                        (lines.Count == 0 ? "(empty)" : string.Join(" | ", lines)));
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _rowOcrBusy, 0);
            // A crop may have been queued after we decided to exit but before
            // busy cleared — start another worker if so.
            var restart = false;
            lock (_rowOcrGate)
            {
                restart = _pendingRowBgra != null;
            }

            if (restart && Interlocked.CompareExchange(ref _rowOcrBusy, 1, 0) == 0)
                _ = Task.Run(() => RowOcrWorker(dumpRect));
        }
    }

    /// <summary>
    /// Shop item ink is small. Pad the crop, then try a 2× nearest scale when
    /// the native read is empty — same approach as the minimap title.
    /// </summary>
    private static IReadOnlyList<string> ReadShopRows(byte[] bgra, int width, int height)
    {
        var padded = PadForOcr(bgra, width, height, 20);
        var lines = WindowsOcr.ReadLines(padded.Pixels, padded.Width, padded.Height);
        if (lines.Count > 0) return lines;
        var scaled = GameFrame.PointScale(padded.Pixels, padded.Width, padded.Height, padded.Width * 2, padded.Height * 2);
        return WindowsOcr.ReadLines(scaled, padded.Width * 2, padded.Height * 2);
    }

    private static string MapName(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var gate = FmMapGate.Classify(line);
            if (gate.Ok) return gate.MatchedName ?? line.Trim();
            var room = Regex.Match(line, @"free\s*market\s*(?:<\s*([0-9li|I!]{1,2})\s*>|\b([0-9li|I!]{1,2})\b)?", RegexOptions.IgnoreCase);
            if (!room.Success) continue;
            var number = room.Groups[1].Success ? room.Groups[1].Value : room.Groups[2].Value;
            if (number.Length > 0)
            {
                var digits = new string(number.Select(ch => ch is 'l' or 'L' or 'i' or 'I' or '|' or '!' ? '1' : ch).ToArray());
                if (int.TryParse(digits, out var roomNumber) && roomNumber is >= 1 and <= 22)
                {
                    return $"Free Market<{roomNumber}>";
                }
            }

            return "Free Market";
        }

        return "";
    }

    private static int? Channel(IReadOnlyList<string> mapLines, IReadOnlyList<string> titleLines)
    {
        foreach (var line in mapLines.Concat(titleLines))
        {
            var match = ChannelText.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var channel) && channel is >= 1 and <= 30)
            {
                return channel;
            }
        }

        return null;
    }
}
