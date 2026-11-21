using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Fingerprint for whether a live shop frame should re-upload. Includes the
/// owner and shop name so a different store with the same items still counts
/// as a new listing set.
/// </summary>
public static class FmListingSignature
{
    public static string From(FmFrameInput frame)
    {
        frame.Rows ??= [];
        var owner = Clean(frame.Owner).ToLowerInvariant();
        var shop = Clean(frame.ShopName).ToLowerInvariant();
        var rows = string.Join(
            "\n",
            frame.Rows.Select(row => $"{Clean(row.Item)}|{row.Quantity}|{row.PriceEach}|{(row.Sold ? 1 : 0)}"));
        return $"{owner}|{shop}\n{rows}";
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }
}
