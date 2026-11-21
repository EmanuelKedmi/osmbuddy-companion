using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Models;

namespace MarketNotes.Companion.Scanning;

public interface IStoreScanner
{
    string Name { get; }

    /// <summary>
    /// True only when the implementation reads shop text from pixels.
    /// The sample scanner returns false.
    /// </summary>
    bool IsLiveOcr { get; }

    StoreScan Scan(StoreScanRequest request);
}

public sealed class StoreScanRequest
{
    public required CapturedFrame? Frame { get; init; }
    public required string WindowTitle { get; init; }
    public required string Owner { get; init; }
    public required string Location { get; init; }
}

public sealed class StoreScan
{
    public required bool IsSampleData { get; init; }
    public required string Status { get; init; }
    public required CompanionStore Store { get; init; }
}
