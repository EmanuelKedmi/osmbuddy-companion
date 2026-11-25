using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketNotes.Companion.Overlay;

public sealed class HoverPricePoint
{
    public string Item { get; set; } = "";
    public long PriceEach { get; set; }
    /// <summary>ISO-8601. Shop asks and sales both land here, oldest first when read.</summary>
    public string At { get; set; } = "";
    /// <summary>"shop" or "sold".</summary>
    public string Kind { get; set; } = "";
}

public sealed class HoverShop
{
    public string Item { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Place { get; set; } = "";
    public long PriceEach { get; set; }
    public int Quantity { get; set; }
    public string At { get; set; } = "";
    public bool Open { get; set; }
}

/// <summary>
/// Prices this companion has actually read. The database quote stays in
/// <see cref="Scanning.PriceCatalog"/>. Sample rows are not written.
/// The file is under %LocalAppData%/MarketNotes.Companion.
/// </summary>
public sealed class HoverMarket
{
    private const int MaxRows = 1500;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly List<HoverShop> _shops = [];
    private readonly List<HoverPricePoint> _sales = [];
    private bool _dirty;

    public HoverMarket(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        Load();
    }

    public string Path => _path;

    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, "MarketNotes.Companion", "hover-market.json");
    }

    public static string PlaceOf(string shopName, string mapName)
    {
        var title = shopName.Trim();
        var map = mapName.Trim();
        if (title.Length > 0 && map.Length > 0) return $"{title} · {map}";
        return title.Length > 0 ? title : map;
    }

    public void ObserveShop(string item, string owner, string place, long priceEach, int quantity, DateTimeOffset at, bool sample)
    {
        if (sample || priceEach < 1 || IsSample(item)) return;
        var name = Canonical(item);
        if (name.Length == 0) return;
        var who = owner.Trim();
        var where = place.Trim();
        var stamp = Stamp(at);
        var qty = quantity < 1 ? 1 : quantity;
        var existing = _shops.Find(shop => shop.Open
            && Same(shop.Item, name)
            && Same(shop.Owner, who)
            && Same(shop.Place, where));
        if (existing != null && existing.PriceEach == priceEach)
        {
            if (existing.Quantity == qty) return;
            existing.Quantity = qty;
            existing.At = stamp;
            _dirty = true;
            return;
        }

        if (existing != null) existing.Open = false;
        _shops.Add(new HoverShop
        {
            Item = name,
            Owner = who,
            Place = where,
            PriceEach = priceEach,
            Quantity = qty,
            At = stamp,
            Open = true,
        });
        _dirty = true;
        Trim();
    }

    public void ObserveSale(string item, long priceEach, DateTimeOffset at, bool sample)
    {
        if (sample || priceEach < 1 || IsSample(item)) return;
        var name = Canonical(item);
        if (name.Length == 0) return;
        _sales.Add(new HoverPricePoint
        {
            Item = name,
            PriceEach = priceEach,
            At = Stamp(at),
            Kind = "sold",
        });
        _dirty = true;
        Trim();
    }

    /// <summary>A hired merchant left the screen. Earlier prices stay for the trend.</summary>
    public void CloseOwner(string owner)
    {
        var who = owner.Trim();
        if (who.Length == 0) return;
        var changed = false;
        foreach (var shop in _shops)
        {
            if (!shop.Open || !Same(shop.Owner, who)) continue;
            shop.Open = false;
            changed = true;
        }

        if (changed) _dirty = true;
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var file = new HoverMarketFile { Shops = _shops, Sales = _sales };
            File.WriteAllText(_path, JsonSerializer.Serialize(file, Options));
            _dirty = false;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public IReadOnlyList<HoverPricePoint> Trend(string item)
    {
        var name = Canonical(item);
        if (name.Length == 0) return [];
        var points = new List<HoverPricePoint>();
        foreach (var shop in _shops)
        {
            if (!Same(shop.Item, name) || shop.PriceEach < 1) continue;
            points.Add(new HoverPricePoint
            {
                Item = shop.Item,
                PriceEach = shop.PriceEach,
                At = shop.At,
                Kind = "shop",
            });
        }

        foreach (var sale in _sales)
        {
            if (Same(sale.Item, name) && sale.PriceEach >= 1) points.Add(sale);
        }

        points.Sort((a, b) => Time(a.At).CompareTo(Time(b.At)));
        return points;
    }

    public IReadOnlyList<HoverShop> ActiveShops(string item)
    {
        var name = Canonical(item);
        if (name.Length == 0) return [];
        return _shops
            .Where(shop => shop.Open && Same(shop.Item, name) && shop.PriceEach >= 1)
            .OrderBy(shop => shop.PriceEach)
            .ThenBy(shop => shop.Owner, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var file = JsonSerializer.Deserialize<HoverMarketFile>(File.ReadAllText(_path), Options);
            if (file == null) return;
            _shops.AddRange(file.Shops.Where(shop => !string.IsNullOrWhiteSpace(shop.Item) && shop.PriceEach >= 1));
            _sales.AddRange(file.Sales.Where(sale => !string.IsNullOrWhiteSpace(sale.Item) && sale.PriceEach >= 1));
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void Trim()
    {
        TrimClosed(_shops);
        while (_sales.Count > MaxRows)
        {
            var oldest = 0;
            for (var i = 1; i < _sales.Count; i++)
            {
                if (Time(_sales[i].At) < Time(_sales[oldest].At)) oldest = i;
            }

            _sales.RemoveAt(oldest);
        }
    }

    private static void TrimClosed(List<HoverShop> shops)
    {
        while (shops.Count > MaxRows)
        {
            var oldest = -1;
            for (var i = 0; i < shops.Count; i++)
            {
                if (shops[i].Open) continue;
                if (oldest < 0 || Time(shops[i].At) < Time(shops[oldest].At)) oldest = i;
            }

            if (oldest < 0)
            {
                oldest = 0;
                for (var i = 1; i < shops.Count; i++)
                {
                    if (Time(shops[i].At) < Time(shops[oldest].At)) oldest = i;
                }
            }

            shops.RemoveAt(oldest);
        }
    }

    private static string Canonical(string item)
    {
        var clean = item.Trim();
        if (clean.Length == 0) return "";
        return Scanning.PriceCatalog.TryRecover(clean, out var canonical) && canonical.Length > 0
            ? canonical
            : clean;
    }

    private static bool IsSample(string item) =>
        item.Trim().StartsWith("[Sample]", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static long Time(string at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.UtcTicks
            : 0;

    private sealed class HoverMarketFile
    {
        public int Version { get; set; } = 1;
        public List<HoverShop> Shops { get; set; } = [];
        public List<HoverPricePoint> Sales { get; set; } = [];
    }
}
