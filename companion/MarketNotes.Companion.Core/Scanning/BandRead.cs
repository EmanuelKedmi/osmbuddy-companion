namespace MarketNotes.Companion.Scanning;

/// <summary>
/// When to OCR a fixed screen band. Changed pixels are always read. Unchanged
/// pixels are read again every <see cref="RetryMs"/> while the label is still
/// unknown, so a static title or name box that missed on the first read is
/// not stuck until the next map change repaints it.
/// </summary>
public sealed class BandRead
{
    private string? _hash;
    private long? _lastRead;

    public BandRead(long retryMs = 2000)
    {
        RetryMs = retryMs;
    }

    public long RetryMs { get; }

    public bool ShouldRead(string hash, bool settled, long nowMs)
    {
        if (!string.Equals(hash, _hash, StringComparison.Ordinal))
        {
            _hash = hash;
            _lastRead = nowMs;
            return true;
        }

        if (settled) return false;
        if (_lastRead is long last && nowMs - last < RetryMs) return false;
        _lastRead = nowMs;
        return true;
    }

    public void Reset()
    {
        _hash = null;
        _lastRead = null;
    }
}
