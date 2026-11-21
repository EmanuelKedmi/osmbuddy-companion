namespace MarketNotes.Companion.Models;

public sealed class ShopRowView
{
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
    public long PriceEach { get; set; }
    public int? Slot { get; set; }
    public string DbPrice { get; set; } = "—";
    public string DbLabel { get; set; } = "";
    public string Gap { get; set; } = "";
    public long? DbMeso { get; set; }
    public bool Sold { get; set; }
    public bool DebugOnly { get; set; }
    public string SoldLabel => Sold ? "Sold" : "";
}
