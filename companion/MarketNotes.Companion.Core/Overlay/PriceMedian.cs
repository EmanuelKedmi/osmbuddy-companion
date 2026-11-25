namespace MarketNotes.Companion.Overlay;

/// <summary>
/// The middle price of listings or of sales. The middle half of the prices
/// is the bulk. A price in the top or bottom quarter is left out when it is
/// at least 5× away from that bulk, so a 20,000,000 or 500,000,000 tag is
/// dropped only when most listings are somewhere else. With fewer than four
/// prices, an end at least 10× from the other is left out, and two prices
/// that far apart have no middle.
/// </summary>
public static class PriceMedian
{
    private const long QuarterGap = 5;
    private const long FewGap = 10;

    public readonly record struct Run(long Low, long High)
    {
        public bool Contains(long price) => price >= Low && price <= High;
    }

    public static long? Of(IEnumerable<long> prices)
    {
        var kept = Keep(prices);
        if (kept.Count == 0) return null;
        return Middle(kept);
    }

    /// <summary>The prices that survive, as a low and a high. Null when none do.</summary>
    public static Run? Cluster(IEnumerable<long> prices)
    {
        var kept = Keep(prices);
        if (kept.Count == 0) return null;
        return new Run(kept[0], kept[^1]);
    }

    private static List<long> Keep(IEnumerable<long> prices)
    {
        var sorted = prices.Where(price => price >= 1).OrderBy(price => price).ToList();
        while (sorted.Count >= 4)
        {
            var cut = sorted.Count / 4;
            if (cut < 1) break;
            var reference = Middle(sorted.GetRange(cut, sorted.Count - 2 * cut));
            if (reference < 1) break;
            var next = sorted.Where((price, index) =>
                index >= cut && index < sorted.Count - cut
                || (index >= sorted.Count - cut && !FarAbove(price, reference))
                || (index < cut && !FarBelow(price, reference))).ToList();
            if (next.Count == sorted.Count) break;
            sorted = next;
        }

        if (sorted.Count == 2 && FarAbove(sorted[1], sorted[0], FewGap)) return [];
        if (sorted.Count >= 3)
        {
            var reference = Middle(sorted);
            sorted = sorted.Where(price => !FarAbove(price, reference, FewGap) && !FarBelow(price, reference, FewGap)).ToList();
        }

        return sorted;
    }

    private static bool FarAbove(long price, long reference, long gap = QuarterGap) =>
        reference >= 1 && price / reference >= gap;

    private static bool FarBelow(long price, long reference, long gap = QuarterGap) =>
        price >= 1 && reference / price >= gap;

    private static long Middle(List<long> sorted)
    {
        var mid = sorted.Count / 2;
        if (sorted.Count % 2 == 1) return sorted[mid];
        var low = sorted[mid - 1];
        var high = sorted[mid];
        return low + (high - low) / 2;
    }
}
