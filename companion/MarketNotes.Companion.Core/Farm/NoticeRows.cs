namespace MarketNotes.Companion.Farm;

/// <summary>A read line and its top edge in crop pixels.</summary>
public readonly record struct PlacedLine(string Text, int Top);

/// <summary>One detected text box, in crop pixels.</summary>
public readonly record struct PlacedBox(string Text, double Left, double Top, double Right, double Bottom);

public static class NoticeRows
{
    /// <summary>
    /// Joins boxes that share a row, left to right. Mid-animation frames make the
    /// detector split one notice into pieces ("You have gained mesos" and "(+284)"),
    /// and each piece alone fails the parser.
    /// </summary>
    /// <param name="splitGaps">
    /// Loot notices: a wide gap ends a line. Off for the battle panel, where each
    /// label stands far left of its right-aligned value.
    /// </param>
    public static IReadOnlyList<PlacedLine> Join(IEnumerable<PlacedBox> boxes, bool splitGaps = true)
    {
        var kept = boxes.Where(box => box.Text.Length > 0).OrderBy(box => box.Top).ToList();
        var heights = kept.Select(box => box.Bottom - box.Top).Order().ToList();
        var usual = heights.Count == 0 ? 0 : heights[(heights.Count - 1) / 2];
        var rows = new List<List<PlacedBox>>();
        foreach (var box in kept)
        {
            var row = Tall(box, usual) ? null : rows.FirstOrDefault(row => !Tall(row[0], usual) && SameRow(row[0], box));
            if (row == null) rows.Add([box]);
            else row.Add(box);
        }

        return rows
            .SelectMany(row => splitGaps ? Pieces(row, usual) : [row.OrderBy(box => box.Left).ToList()])
            .Select(piece => new PlacedLine(
                string.Join(' ', piece.Select(box => box.Text)),
                (int)Math.Round(Top(piece, usual))))
            .OrderBy(line => line.Top)
            .ToList();
    }

    /// <summary>
    /// Splits a row where boxes stand far apart: a notice's pieces are a space
    /// apart, a battle panel row beside it is not part of it.
    /// </summary>
    private static IEnumerable<List<PlacedBox>> Pieces(List<PlacedBox> row, double usual)
    {
        var piece = new List<PlacedBox>();
        foreach (var box in row.OrderBy(box => box.Left))
        {
            if (piece.Count > 0 && usual > 0 && box.Left - piece[^1].Right > usual * 2)
            {
                yield return piece;
                piece = [];
            }

            piece.Add(box);
        }

        if (piece.Count > 0) yield return piece;
    }

    /// <summary>
    /// A name plate or title walking under the corner fuses with the notice it
    /// touches into one tall box. The notice is the bottom of it: Track() takes
    /// a line seen higher than before for a different line.
    /// </summary>
    private static double Top(List<PlacedBox> row, double usual)
    {
        var top = row.Min(box => box.Top);
        var bottom = row.Max(box => box.Bottom);
        return usual > 0 && bottom - top > usual * 1.5 ? bottom - usual : top;
    }

    /// <summary>
    /// A box spanning several rows (a notice drifting over the battle panel) would
    /// pull every row it touches into one line, so it stays on its own.
    /// </summary>
    private static bool Tall(PlacedBox box, double usual) => usual > 0 && box.Bottom - box.Top > usual * 1.5;

    /// <summary>Measured against the row's first box, so a row cannot grow down the panel.</summary>
    private static bool SameRow(PlacedBox first, PlacedBox box)
    {
        var overlap = Math.Min(first.Bottom, box.Bottom) - Math.Max(first.Top, box.Top);
        var shorter = Math.Min(first.Bottom - first.Top, box.Bottom - box.Top);
        return shorter > 0 && overlap >= shorter * 0.5;
    }
}
