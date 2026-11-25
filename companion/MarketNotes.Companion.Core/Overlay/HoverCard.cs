using System.Globalization;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// One stat range on a piece of gear. Empty until a later reader fills it.
/// The numbers sit in the same card as the price, beside the game tooltip.
/// </summary>
public readonly record struct GearRoll(string Stat, int Min, int Average, int Max);

public sealed class HoverLine
{
    public required string Text { get; init; }
    public bool Emphasis { get; init; }
}

public sealed class HoverSectionView
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<HoverLine> Lines { get; init; }
    /// <summary>0 is the low price, 1 is the high. Empty when there is nothing to plot.</summary>
    public IReadOnlyList<double> Trend { get; init; } = [];
    public bool CanExpand { get; init; }
    public bool Expanded { get; init; }
    public string ExpandLabel { get; init; } = "";
}

public sealed class HoverCardView
{
    public required string ItemName { get; init; }
    public required IReadOnlyList<HoverSectionView> Sections { get; init; }
}

/// <summary>
/// What the card knows about the hovered item. A future section reads this
/// and adds nothing else to the window.
/// </summary>
public sealed class HoverRead
{
    public required string ItemName { get; init; }
    public PriceQuote Quote { get; set; }
    public IReadOnlyList<HoverPricePoint> Trend { get; set; } = [];
    public IReadOnlyList<long> ListingPrices { get; set; } = [];
    public IReadOnlyList<long> SalePrices { get; set; } = [];
    /// <summary>Set while the website scan is not ready. Empty when the medians can be read.</summary>
    public string? MarketStatus { get; set; }
    public IReadOnlyList<HoverShop> Shops { get; set; } = [];
    public IReadOnlyList<GearRoll> Rolls { get; set; } = [];
}

public interface IHoverSection
{
    string Id { get; }
    /// <summary>Lower draws first, under the item name.</summary>
    int Order { get; }
    HoverSectionView? Build(HoverRead read, bool expanded);
}

public sealed record InGameOption(string Id, string Title, string Detail);

/// <summary>
/// The stack beside the game's item tooltip. A new option is a class here
/// plus a row in <see cref="Options"/>. Order is the draw order.
/// </summary>
public static class HoverSections
{
    public static IReadOnlyList<IHoverSection> All { get; } =
    [
        new PriceTrendSection(),
        new ActiveShopsSection(),
        new GearRollSection(),
    ];

    public static IReadOnlyList<InGameOption> Options { get; } =
    [
        new("price-trend", "Price trend", "Database price, median listing and median sale from the website, and the low, latest, and high from shops this companion has read."),
        new("active-shops", "Active shops", "Shops still listing the item. The card expands to the owner, place, and price."),
        new("gear-rolls", "Gear rolls", "Min, average, and max of each stat on a dropped equip, beside the game tooltip."),
    ];

    public static HoverCardView? Compose(HoverRead read, Func<string, bool> enabled, Func<string, bool> expanded)
    {
        if (string.IsNullOrWhiteSpace(read.ItemName)) return null;
        var sections = new List<HoverSectionView>();
        foreach (var section in All.OrderBy(item => item.Order))
        {
            if (!enabled(section.Id)) continue;
            var view = section.Build(read, expanded(section.Id));
            if (view != null) sections.Add(view);
        }

        if (sections.Count == 0) return null;
        return new HoverCardView { ItemName = read.ItemName.Trim(), Sections = sections };
    }

    public static string Meso(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed class PriceTrendSection : IHoverSection
{
    public string Id => "price-trend";
    public int Order => 10;

    public HoverSectionView Build(HoverRead read, bool expanded)
    {
        var lines = new List<HoverLine>();
        if (read.Quote.Meso is long meso)
        {
            lines.Add(new HoverLine { Text = $"{read.Quote.Label} · {HoverSections.Meso(meso)}", Emphasis = true });
        }
        else
        {
            lines.Add(new HoverLine { Text = read.Quote.Label });
        }

        if (!string.IsNullOrWhiteSpace(read.MarketStatus))
        {
            lines.Add(new HoverLine { Text = read.MarketStatus });
        }
        else
        {
            lines.Add(MedianLine("Median listing", PriceMedian.Of(read.ListingPrices)));
            lines.Add(MedianLine("Median sales", PriceMedian.Of(read.SalePrices)));
        }

        var cluster = PriceMedian.Cluster(read.Trend.Select(point => point.PriceEach));
        var points = read.Trend
            .Where(point => point.PriceEach >= 1 && cluster != null && cluster.Value.Contains(point.PriceEach))
            .OrderBy(point => point.At, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<double> spark = [];
        if (points.Count == 1)
        {
            lines.Add(new HoverLine { Text = $"One price on record · {HoverSections.Meso(points[0].PriceEach)}" });
        }
        else if (points.Count >= 2)
        {
            var low = points.Min(point => point.PriceEach);
            var high = points.Max(point => point.PriceEach);
            var latest = points[^1].PriceEach;
            lines.Add(new HoverLine { Text = $"Low {HoverSections.Meso(low)} · latest {HoverSections.Meso(latest)} · high {HoverSections.Meso(high)}" });
            spark = Normalize(points, low, high);
        }

        return new HoverSectionView
        {
            Id = Id,
            Title = "Price trend",
            Lines = lines,
            Trend = spark,
        };
    }

    private static HoverLine MedianLine(string label, long? price) => new()
    {
        Text = price is long meso ? $"{label} · {HoverSections.Meso(meso)}" : $"{label} · none yet",
        Emphasis = price != null,
    };

    private static IReadOnlyList<double> Normalize(List<HoverPricePoint> points, long low, long high)
    {
        var span = high - low;
        if (span <= 0) return points.Select(_ => 0.5).ToList();
        return points.Select(point => (point.PriceEach - low) / (double)span).ToList();
    }
}

public sealed class ActiveShopsSection : IHoverSection
{
    public string Id => "active-shops";
    public int Order => 20;

    public HoverSectionView Build(HoverRead read, bool expanded)
    {
        var shops = read.Shops.Where(shop => shop.Open && shop.PriceEach >= 1)
            .OrderBy(shop => shop.PriceEach)
            .ThenBy(shop => shop.Owner, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lines = new List<HoverLine>
        {
            new()
            {
                Text = shops.Count switch
                {
                    0 => "No active shop",
                    1 => "1 active shop",
                    _ => $"{shops.Count} active shops",
                },
            },
        };
        if (expanded)
        {
            foreach (var shop in shops)
            {
                var who = string.IsNullOrWhiteSpace(shop.Owner) ? "Shop" : shop.Owner.Trim();
                var where = shop.Place.Trim();
                var place = where.Length == 0 ? who : $"{who} · {where}";
                var qty = shop.Quantity < 1 ? 1 : shop.Quantity;
                lines.Add(new HoverLine { Text = $"{place} · {HoverSections.Meso(shop.PriceEach)} × {qty}" });
            }
        }

        return new HoverSectionView
        {
            Id = Id,
            Title = "Active shops",
            Lines = lines,
            CanExpand = shops.Count > 0,
            Expanded = expanded && shops.Count > 0,
            ExpandLabel = expanded ? "Hide shops" : "Show shops",
        };
    }
}

public sealed class GearRollSection : IHoverSection
{
    public string Id => "gear-rolls";
    public int Order => 30;

    public HoverSectionView? Build(HoverRead read, bool expanded)
    {
        if (read.Rolls.Count == 0) return null;
        var lines = read.Rolls
            .Where(roll => !string.IsNullOrWhiteSpace(roll.Stat))
            .Select(roll => new HoverLine
            {
                Text = $"{roll.Stat.Trim()} · min {roll.Min} · avg {roll.Average} · max {roll.Max}",
            })
            .ToList();
        if (lines.Count == 0) return null;
        return new HoverSectionView
        {
            Id = Id,
            Title = "Rolls",
            Lines = lines,
        };
    }
}

/// <summary>Open or closed, and which sections the card may draw.</summary>
public sealed class InGameUi
{
    private readonly HashSet<string> _enabled;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    public InGameUi()
    {
        _enabled = HoverSections.Options.Select(option => option.Id).ToHashSet(StringComparer.Ordinal);
    }

    public bool Open { get; private set; }

    /// <summary>True while a section on the menu is selected. The menu itself can be closed.</summary>
    public bool AnyEnabled => _enabled.Count > 0;

    public void Toggle() => Open = !Open;

    public void SetOpen(bool open) => Open = open;

    public bool IsEnabled(string id) => _enabled.Contains(id);

    public void SetEnabled(string id, bool on)
    {
        if (on) _enabled.Add(id);
        else _enabled.Remove(id);
    }

    public bool IsExpanded(string id) => _expanded.Contains(id);

    public void ToggleExpanded(string id)
    {
        if (!_expanded.Add(id)) _expanded.Remove(id);
    }

    public IReadOnlyList<InGameOption> Options => HoverSections.Options;
}
