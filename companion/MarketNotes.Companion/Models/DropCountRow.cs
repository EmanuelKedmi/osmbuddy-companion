namespace MarketNotes.Companion.Models;

public sealed class DropCountRow
{
    public string Item { get; set; } = "";
    public long Count { get; set; }
    public string SessionRate { get; set; } = "—";
    public string TotalRate { get; set; } = "—";
    /// <summary>Raw kills per unit. The grid sorts on this, not the label. Missing rates sort as rarest.</summary>
    public double SessionKillsPerDrop { get; set; } = double.PositiveInfinity;
    public double TotalKillsPerDrop { get; set; } = double.PositiveInfinity;
}
