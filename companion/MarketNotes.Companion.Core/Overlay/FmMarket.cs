using System.Text.Json;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// Listing asks and sales from GET /api/fm. Open shops only. A deleted row
/// is not a listing. Names use the catalog spelling when the scan has one.
/// </summary>
public static class FmMarket
{
    public sealed class Prices
    {
        public List<long> Listings { get; } = [];
        public List<long> Sales { get; } = [];
    }

    public static Dictionary<string, Prices> Read(string json, string? only = null)
    {
        var wanted = string.IsNullOrWhiteSpace(only) ? null : Key(only);
        var items = new Dictionary<string, Prices>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) return items;
        var scan = root.TryGetProperty("scan", out var wrapped) ? wrapped : root;
        if (scan.TryGetProperty("shops", out var shops))
        {
            foreach (var shop in shops.EnumerateArray())
            {
                if (shop.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.False) continue;
                if (!shop.TryGetProperty("lines", out var lines)) continue;
                foreach (var line in lines.EnumerateArray())
                {
                    if (line.TryGetProperty("status", out var status)
                        && status.ValueKind == JsonValueKind.String
                        && string.Equals(status.GetString(), "deleted", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Add(items, Name(line), Price(line), sale: false, wanted);
                }
            }
        }

        if (scan.TryGetProperty("sold", out var sold))
        {
            foreach (var sale in sold.EnumerateArray())
            {
                Add(items, Name(sale), Price(sale), sale: true, wanted);
            }
        }

        return items;
    }

    /// <summary>One item from a scan. A body that is only that item's prices is returned as-is.</summary>
    public static Prices ReadItem(string json, string item)
    {
        var direct = DirectLists(json);
        if (direct != null) return direct;
        var key = Key(item);
        var items = Read(json, key);
        return key.Length > 0 && items.TryGetValue(key, out var row) ? row : new Prices();
    }

    /// <summary>
    /// Listings and sales sent for one item, with no shop list. A full scan returns null.
    /// </summary>
    public static Prices? DirectLists(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("scan", out _)) return null;
        var hasListings = root.TryGetProperty("listings", out var listings) && listings.ValueKind == JsonValueKind.Array;
        var hasSales = root.TryGetProperty("sales", out var sales) && sales.ValueKind == JsonValueKind.Array;
        if (!hasListings && !hasSales) return null;
        var prices = new Prices();
        if (hasListings) Append(prices.Listings, listings);
        if (hasSales) Append(prices.Sales, sales);
        return prices;
    }

    private static void Append(List<long> into, JsonElement values)
    {
        foreach (var value in values.EnumerateArray())
        {
            var price = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var whole) => whole,
                JsonValueKind.Number => (long)value.GetDouble(),
                _ => 0,
            };
            if (price >= 1) into.Add(price);
        }
    }

    public static string Key(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0) return "";
        return PriceCatalog.TryRecover(clean, out var canonical) && canonical.Length > 0 ? canonical : clean;
    }

    private static void Add(Dictionary<string, Prices> items, string name, long price, bool sale, string? only)
    {
        if (price < 1 || name.Length == 0) return;
        var key = Key(name);
        if (key.Length == 0) return;
        if (only != null && !string.Equals(key, only, StringComparison.OrdinalIgnoreCase)) return;
        if (!items.TryGetValue(key, out var row))
        {
            row = new Prices();
            items[key] = row;
        }

        if (sale) row.Sales.Add(price);
        else row.Listings.Add(price);
    }

    private static string Name(JsonElement row) =>
        row.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? ""
            : "";

    private static long Price(JsonElement row)
    {
        if (!row.TryGetProperty("priceEach", out var price)) return 0;
        return price.ValueKind switch
        {
            JsonValueKind.Number when price.TryGetInt64(out var whole) => whole,
            JsonValueKind.Number => (long)price.GetDouble(),
            _ => 0,
        };
    }
}
