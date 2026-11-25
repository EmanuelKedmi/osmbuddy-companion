namespace MarketNotes.Companion.Overlay;

public readonly record struct PlacedRect(int X, int Y, int Width, int Height);

/// <summary>
/// Where the panel and the hover card sit inside the game client, in pixels.
/// The card stays beside the game's own tooltip.
/// </summary>
public static class NotesLayout
{
    public static PlacedRect Panel(int clientWidth, int clientHeight, int panelWidth, int panelHeight)
    {
        var width = Math.Max(1, panelWidth);
        var height = Math.Max(1, panelHeight);
        var x = Math.Max(8, clientWidth - width - 16);
        var y = 16;
        if (y + height > clientHeight - 8) y = Math.Max(8, clientHeight - height - 8);
        return new PlacedRect(x, y, width, height);
    }

    public static PlacedRect BesideTooltip(
        int tooltipX,
        int tooltipY,
        int tooltipWidth,
        int tooltipHeight,
        int clientWidth,
        int clientHeight,
        int cardWidth,
        int cardHeight,
        int gap = 8)
    {
        var width = Math.Max(1, cardWidth);
        var height = Math.Max(1, cardHeight);
        var x = tooltipX + Math.Max(0, tooltipWidth) + gap;
        if (x + width > clientWidth - 8)
        {
            x = tooltipX - gap - width;
        }

        if (x < 8)
        {
            x = Math.Max(8, Math.Min(tooltipX + Math.Max(0, tooltipWidth) + gap, Math.Max(8, clientWidth - width - 8)));
        }

        var y = tooltipY;
        if (y + height > clientHeight - 8) y = Math.Max(8, clientHeight - height - 8);
        if (y < 8) y = 8;
        return new PlacedRect(x, y, width, height);
    }
}
