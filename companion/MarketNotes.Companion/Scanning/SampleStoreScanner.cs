using System.Globalization;
using MarketNotes.Companion.Models;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Manual stub used when no OCR engine is installed.
/// Every line is prefixed with "[Sample]". These are not live shop rows.
/// </summary>
public sealed class SampleStoreScanner : IStoreScanner
{
    public const string SamplePrefix = "[Sample]";

    public string Name => "Sample scanner";
    public bool IsLiveOcr => false;

    public StoreScan Scan(StoreScanRequest request)
    {
        var captured = request.Frame is { Width: > 0, Height: > 0 };
        var status = captured
            ? $"SAMPLE DATA. Grabbed {request.Frame!.Width}×{request.Frame.Height} pixels from \"{request.WindowTitle}\". No OCR engine is installed, so the lines below were not read from that window."
            : "SAMPLE DATA. No pixels were grabbed from the chosen window. No OCR engine is installed, so the lines below are not a live shop.";

        var owner = string.IsNullOrWhiteSpace(request.Owner)
            ? $"{SamplePrefix} Mushmom Shop"
            : request.Owner.Trim();
        var location = string.IsNullOrWhiteSpace(request.Location)
            ? $"{SamplePrefix} FM Channel 1"
            : request.Location.Trim();

        var store = new CompanionStore
        {
            Owner = owner,
            Location = location,
            ScannedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            Lines =
            [
                Line($"{SamplePrefix} Scroll for Gloves for DEX 10%", 1, 350_000, 1),
                Line($"{SamplePrefix} White Scroll", 2, 8_000_000, 2),
                Line($"{SamplePrefix} Chaos Scroll 60%", 5, 900_000, 3),
                Line($"{SamplePrefix} Diamond Ore", 100, 4_000, 4),
            ],
        };

        return new StoreScan
        {
            IsSampleData = true,
            Status = status,
            Store = store,
        };
    }

    private static StoreLine Line(string item, int quantity, long priceEach, int slot) => new()
    {
        Item = item,
        Quantity = quantity,
        PriceEach = priceEach,
        Slot = slot,
    };
}
