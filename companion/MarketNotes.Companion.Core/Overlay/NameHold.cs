namespace MarketNotes.Companion.Overlay;

/// <summary>
/// The item name, once the tooltip title has finished drawing. The card
/// stays on that name while later frames shimmer. A new name replaces it
/// only after the same read comes back again, or the same pixels last into
/// the next sample.
/// </summary>
public sealed class NameHold
{
    private int? _hash;
    private int _frame = -1;
    private string? _pending;
    private string? _accepted;

    /// <summary>The newest read, including one that is not confirmed yet.</summary>
    public string? Latest => _pending ?? _accepted;

    /// <summary>A name has been read, so the next sample of the same pixels can accept it without reading again.</summary>
    public bool HasCandidate => _pending != null || _accepted != null;

    public string? Ready(int hash, int frame, Func<string?> read)
    {
        if (_hash != hash)
        {
            _hash = hash;
            _frame = frame;
            var name = Blank(read());
            if (name != null && string.Equals(name, _pending, StringComparison.Ordinal)) _accepted = name;
            else if (name != null) _pending = name;
        }
        else if (frame != _frame)
        {
            _frame = frame;
            if (_pending != null) _accepted = _pending;
            else
            {
                var name = Blank(read());
                if (name != null) _pending = name;
            }
        }

        return _accepted;
    }

    public void Reset()
    {
        _hash = null;
        _frame = -1;
        _pending = null;
        _accepted = null;
    }

    private static string? Blank(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
