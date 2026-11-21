using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Scanning;

public sealed class FmScrollbar
{
    public double Position { get; set; }
    public int First { get; set; }
    public int Visible { get; set; }
}

public sealed class FmVisibleRow
{
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
    public long PriceEach { get; set; }
    public int? Slot { get; set; }
    public int Row { get; set; }
    public bool Sold { get; set; }

    /// <summary>
    /// A cursor or other mark is sitting on this row, so the fill is not a sale.
    /// </summary>
    public bool Covered { get; set; }
}

public sealed class FmFrameInput
{
    public string? MapName { get; set; }
    public string? Owner { get; set; }
    public string? ShopName { get; set; }
    public int? Channel { get; set; }
    public FmScrollbar? Scrollbar { get; set; }
    public List<FmVisibleRow> Rows { get; set; } = [];
    public int Unreadable { get; set; }
    public string? Error { get; set; }
}

public sealed class FmShopLine
{
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
    public long PriceEach { get; set; }
    public int? Slot { get; set; }
    public int Row { get; set; }
    public string Identity { get; set; } = "";
    public bool Sold { get; set; }
}

public sealed class FmShopState
{
    public string Owner { get; set; } = "";
    public string ShopName { get; set; } = "";
    public string MapName { get; set; } = "";
    public int? Channel { get; set; }
    public FmScrollbar Scrollbar { get; set; } = new();
    public List<FmShopLine> Lines { get; set; } = [];
}

public sealed class FmStep
{
    public required string Kind { get; init; }
    public required string Message { get; init; }
    public bool Reset { get; init; }
    public string? Reason { get; init; }
    public List<FmShopLine> Added { get; init; } = [];
}

/// <summary>
/// Shop state for one share. The same owner, shop, map, channel, and
/// scrollbar are not scanned again. A scrollbar move returns only rows whose
/// slot was outside the previous slice. An owner or shop change clears the shop.
/// </summary>
public sealed class FmScanMachine
{
    private const int MaxLines = 200;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private List<int> _visibleAbs = [];
    public FmShopState? Shop { get; private set; }
    public List<string> Log { get; } = [];

    public void Reset()
    {
        Shop = null;
        _seen.Clear();
        _visibleAbs = [];
        Log.Clear();
    }

    public FmStep Step(FmFrameInput frame)
    {
        frame.Rows ??= [];
        if (!string.IsNullOrWhiteSpace(frame.Error))
        {
            var error = new FmStep { Kind = "error", Message = frame.Error.Trim() };
            Log.Add(error.Message);
            return error;
        }

        var gate = FmMapGate.Classify(frame.MapName);
        if (!gate.Ok)
        {
            var skipped = new FmStep
            {
                Kind = "skipped",
                Message = $"{gate.Detected} — not an FM map",
                Reason = "not an FM map",
            };
            Log.Add(skipped.Message);
            return skipped;
        }

        var scrollbar = Normalize(frame.Scrollbar, frame.Rows.Count);
        var channel = frame.Channel ?? gate.ChannelFromName;
        var owner = Clean(frame.Owner);
        if (owner.Length == 0) owner = "Unknown owner";
        var shopName = Clean(frame.ShopName);
        if (shopName.Length == 0) shopName = "Unknown shop";
        var mapName = Clean(frame.MapName);
        if (mapName.Length == 0) mapName = gate.MatchedName ?? gate.Detected;
        var nextKey = Key(owner, shopName, gate.MatchedName ?? mapName, channel);
        var previous = Shop;
        var previousKey = previous == null
            ? null
            : Key(previous.Owner, previous.ShopName, FmMapGate.Classify(previous.MapName).MatchedName ?? previous.MapName, previous.Channel);
        var reset = !string.Equals(previousKey, nextKey, StringComparison.Ordinal);

        if (!reset && previous != null && SameSlice(previous.Scrollbar, scrollbar))
        {
            var held = new FmStep { Kind = "unchanged", Message = "Scrollbar unchanged. Skipped a rescan." };
            Log.Add(held.Message);
            return held;
        }

        var previousAbs = reset ? [] : _visibleAbs.ToHashSet();
        if (reset)
        {
            _seen.Clear();
            Shop = new FmShopState();
        }

        var added = new List<FmShopLine>();
        var visible = new List<int>(frame.Rows.Count);
        foreach (var row in frame.Rows)
        {
            var abs = row.Slot is >= 0 ? row.Slot.Value : scrollbar.First + Math.Max(0, row.Row);
            visible.Add(abs);
        }

        foreach (var row in CollapseOffers(frame.Rows))
        {
            var abs = row.Slot is >= 0 ? row.Slot.Value : scrollbar.First + Math.Max(0, row.Row);
            var item = row.Item;
            if (item.Length == 0 || row.Quantity < 1 || row.PriceEach < 0) continue;
            if (!reset && previousAbs.Contains(abs))
            {
                var existing = Shop!.Lines.FirstOrDefault(line => (line.Slot ?? line.Row) == abs);
                if (existing != null && row.Quantity > existing.Quantity) existing.Quantity = row.Quantity;
                continue;
            }
            var identity = Identity(item, row.Row, scrollbar);
            if (!_seen.Add(identity))
            {
                var existing = Shop!.Lines.FirstOrDefault(line => line.Identity == identity);
                if (existing != null && row.Quantity > existing.Quantity) existing.Quantity = row.Quantity;
                continue;
            }
            if (Shop!.Lines.Count + added.Count >= MaxLines) break;
            var line = new FmShopLine
            {
                Item = item,
                Quantity = row.Quantity,
                PriceEach = row.PriceEach,
                Slot = row.Slot ?? abs,
                Row = row.Row,
                Identity = identity,
            };
            added.Add(line);
            Shop.Lines.Add(line);
        }

        Shop!.Owner = owner;
        Shop.ShopName = shopName;
        Shop.MapName = mapName;
        Shop.Channel = channel;
        Shop.Scrollbar = scrollbar;
        _visibleAbs = visible;
        var count = added.Count;
        var message = reset && previousKey != null
            ? $"Owner or shop changed. Reset the shop and scanned {count} visible {(count == 1 ? "row" : "rows")}."
            : reset
                ? $"Scanned {count} visible {(count == 1 ? "row" : "rows")}."
                : $"Scrollbar moved. Scanned {count} new {(count == 1 ? "row" : "rows")}.";
        var step = new FmStep
        {
            Kind = "scanned",
            Message = message,
            Reset = previousKey != null && reset,
            Added = added,
        };
        Log.Add(message);
        return step;
    }

    /// <summary>
    /// The shop list becomes the rows on screen now. A live frame replaces
    /// the previous listing instead of waiting for a scrollbar index.
    /// </summary>
    public FmStep Replace(FmFrameInput frame)
    {
        frame.Rows ??= [];
        var gate = FmMapGate.Classify(frame.MapName);
        var mapName = gate.Ok
            ? gate.MatchedName ?? Clean(frame.MapName)
            : Clean(frame.MapName);
        if (mapName.Length == 0) mapName = "Map not read";
        var owner = Clean(frame.Owner);
        if (owner.Length == 0) owner = "Unknown owner";
        var shopName = Clean(frame.ShopName);
        if (shopName.Length == 0) shopName = "Shop";
        var scrollbar = Normalize(frame.Scrollbar, frame.Rows.Count);
        _seen.Clear();
        var lines = new List<FmShopLine>();
        foreach (var row in frame.Rows)
        {
            var item = Clean(row.Item);
            if (item.Length == 0 || row.Quantity < 1 || row.PriceEach < 0) continue;
            var abs = row.Slot is >= 0 ? row.Slot.Value : scrollbar.First + Math.Max(0, row.Row);
            var identity = Identity(item, row.Row, scrollbar);
            if (!_seen.Add(identity)) continue;
            lines.Add(new FmShopLine
            {
                Item = item,
                Quantity = row.Quantity,
                PriceEach = row.PriceEach,
                Slot = row.Slot ?? abs,
                Row = row.Row,
                Identity = identity,
            });
        }

        Shop = new FmShopState
        {
            Owner = owner,
            ShopName = shopName,
            MapName = mapName,
            Channel = frame.Channel ?? (gate.Ok ? gate.ChannelFromName : null),
            Scrollbar = scrollbar,
            Lines = lines,
        };
        _visibleAbs = lines.Select(line => line.Slot ?? line.Row).ToList();
        var message = $"Shop list now has {lines.Count} {(lines.Count == 1 ? "row" : "rows")}.";
        Log.Add(message);
        return new FmStep { Kind = "scanned", Message = message, Reset = true, Added = lines };
    }

    /// <summary>
    /// Adds the rows visible now and leaves earlier rows in place. Scrolling
    /// the same shop does not drop what already scrolled off the screen.
    /// A different owner or shop name starts a new list.
    /// </summary>
    public FmStep Absorb(FmFrameInput frame)
    {
        frame.Rows ??= [];
        var owner = Clean(frame.Owner);
        if (owner.Length == 0) owner = "Unknown owner";
        var shopName = Clean(frame.ShopName);
        if (shopName.Length == 0) shopName = "Shop";
        var gate = FmMapGate.Classify(frame.MapName);
        var mapName = gate.Ok
            ? gate.MatchedName ?? Clean(frame.MapName)
            : Clean(frame.MapName);
        if (mapName.Length == 0) mapName = "Map not read";
        var scrollbar = Normalize(frame.Scrollbar, frame.Rows.Count);
        var shopKey = $"{owner.ToLowerInvariant()}|{shopName.ToLowerInvariant()}";
        var previousKey = Shop == null
            ? null
            : $"{Shop.Owner.ToLowerInvariant()}|{Shop.ShopName.ToLowerInvariant()}";
        var reset = !string.Equals(previousKey, shopKey, StringComparison.Ordinal);
        if (reset)
        {
            _seen.Clear();
            Shop = new FmShopState
            {
                Owner = owner,
                ShopName = shopName,
                MapName = mapName,
                Channel = frame.Channel ?? (gate.Ok ? gate.ChannelFromName : null),
                Scrollbar = scrollbar,
            };
        }

        var added = new List<FmShopLine>();
        var nextSlot = Shop!.Lines.Count;
        // Same item at the same price in one frame is one offer. Quantity is
        // how many of that item the frame shows (stack sizes summed), not the
        // default 1 left on each duplicate row.
        foreach (var row in CollapseOffers(frame.Rows))
        {
            var identity = OfferIdentity(row.Item, row.PriceEach);
            if (!_seen.Add(identity))
            {
                var existing = Shop.Lines.FirstOrDefault(line => line.Identity == identity);
                if (existing != null)
                {
                    if (row.Quantity > existing.Quantity) existing.Quantity = row.Quantity;
                    if (row.Sold) existing.Sold = true;
                }

                continue;
            }
            if (Shop.Lines.Count >= MaxLines) break;
            var line = new FmShopLine
            {
                Item = row.Item,
                Quantity = row.Quantity,
                PriceEach = row.PriceEach,
                Slot = nextSlot,
                Row = nextSlot,
                Identity = identity,
                Sold = row.Sold,
            };
            nextSlot++;
            added.Add(line);
            Shop.Lines.Add(line);
        }

        Shop.Owner = owner;
        Shop.ShopName = shopName;
        Shop.MapName = mapName;
        Shop.Channel = frame.Channel ?? Shop.Channel;
        Shop.Scrollbar = scrollbar;
        var total = Shop.Lines.Count;
        var count = added.Count;
        var message = reset && previousKey != null
            ? $"Owner or shop changed. The list started over with {count} {(count == 1 ? "listing" : "listings")}."
            : $"Shop list has {total} {(total == 1 ? "listing" : "listings")}. Added {count} from this frame.";
        Log.Add(message);
        return new FmStep
        {
            Kind = "scanned",
            Message = message,
            Reset = previousKey != null && reset,
            Added = added,
        };
    }

    public static string Identity(string item, int row, FmScrollbar scrollbar)
    {
        var end = scrollbar.First + Math.Max(scrollbar.Visible, 1) - 1;
        return $"{Clean(item).ToLowerInvariant()}|{row}|{scrollbar.First}-{end}";
    }

    /// <summary>Same item at the same price is one offer. Quantity is tracked separately.</summary>
    public static string OfferIdentity(string item, long priceEach)
    {
        return $"{Clean(item).ToLowerInvariant()}|{priceEach}";
    }

    /// <summary>
    /// One row per item and price. Quantities from every matching visible row
    /// are added so a stack of copies is not stored as quantity 1.
    /// </summary>
    public static List<FmVisibleRow> CollapseOffers(IEnumerable<FmVisibleRow> rows)
    {
        var collapsed = new List<FmVisibleRow>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var item = Clean(row.Item);
            if (item.Length == 0 || row.Quantity < 1 || row.PriceEach < 1) continue;
            var key = OfferIdentity(item, row.PriceEach);
            if (index.TryGetValue(key, out var at))
            {
                collapsed[at].Quantity += row.Quantity;
                collapsed[at].Sold = collapsed[at].Sold || row.Sold;
                continue;
            }

            index[key] = collapsed.Count;
            collapsed.Add(new FmVisibleRow
            {
                Item = item,
                Quantity = row.Quantity,
                PriceEach = row.PriceEach,
                Slot = row.Slot,
                Row = row.Row,
                Sold = row.Sold,
                Covered = row.Covered,
            });
        }

        return collapsed;
    }

    private static string Key(string owner, string shop, string map, int? channel)
    {
        return $"{Clean(owner).ToLowerInvariant()}|{Clean(shop).ToLowerInvariant()}|{FmMapGate.Normalize(map)}|{channel?.ToString() ?? ""}";
    }

    private static bool SameSlice(FmScrollbar a, FmScrollbar b)
    {
        return a.First == b.First && a.Visible == b.Visible && Math.Abs(a.Position - b.Position) < 1e-6;
    }

    private static FmScrollbar Normalize(FmScrollbar? scrollbar, int rowCount)
    {
        if (scrollbar == null)
        {
            return new FmScrollbar { Position = 0, First = 0, Visible = Math.Max(rowCount, 1) };
        }

        var position = double.IsFinite(scrollbar.Position) ? Math.Clamp(scrollbar.Position, 0, 1) : 0;
        return new FmScrollbar
        {
            Position = position,
            First = scrollbar.First >= 0 ? scrollbar.First : 0,
            Visible = scrollbar.Visible > 0 ? scrollbar.Visible : Math.Max(rowCount, 1),
        };
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }
}
