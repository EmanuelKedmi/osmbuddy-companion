namespace MarketNotes.Companion.Scanning;

public sealed record SoldListing(
    string Item,
    int Quantity,
    long PriceEach,
    string Owner,
    string ShopName,
    string MapName);

/// <summary>
/// A listing is sold when its row fill is paler than the other rows in the
/// same shop. The row stays on the list until the shop closes.
/// </summary>
public sealed class ListingSales
{
    private string _shopKey = "";
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        _shopKey = "";
        _seen.Clear();
    }

    public IReadOnlyList<SoldListing> Observe(string owner, string shopName, string mapName, IReadOnlyList<FmVisibleRow> visible)
    {
        var key = $"{owner.Trim().ToLowerInvariant()}|{shopName.Trim().ToLowerInvariant()}";
        if (!string.Equals(key, _shopKey, StringComparison.Ordinal))
        {
            _shopKey = key;
            _seen.Clear();
        }

        var sold = new List<SoldListing>();
        foreach (var row in visible)
        {
            if (!row.Sold || row.PriceEach < 1 || string.IsNullOrWhiteSpace(row.Item)) continue;
            var identity = $"{row.Item.Trim().ToLowerInvariant()}|{row.PriceEach}";
            if (!_seen.Add(identity)) continue;
            sold.Add(new SoldListing(row.Item, Math.Max(1, row.Quantity), row.PriceEach, owner, shopName, mapName));
        }

        return sold;
    }
}
