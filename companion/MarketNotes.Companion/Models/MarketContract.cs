namespace MarketNotes.Companion.Models;

/// <summary>
/// JSON names are camelCase via <see cref="Export.ScanExporter"/>.
/// Keep this in step with <c>lib/market.ts</c> and <c>companion/README.md</c>.
/// </summary>
public sealed class StoreLine
{
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
    public long PriceEach { get; set; }
    public int? Slot { get; set; }
}

/// <summary>
/// One shop in a companion export. The site matches owner + location.
/// There is no id here. The book adds one when the shop is new.
/// </summary>
public sealed class CompanionStore
{
    public string Owner { get; set; } = "";
    public string Location { get; set; } = "";
    public string ScannedAt { get; set; } = "";
    public List<StoreLine> Lines { get; set; } = [];
}

public sealed class CompanionScanExport
{
    public int Version { get; set; } = 1;
    public string Kind { get; set; } = "store-scan";
    public List<CompanionStore> Stores { get; set; } = [];
}
