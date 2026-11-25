using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Modes;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// Listings and sales for the item under the pointer. The request is only
/// that item. The answer is kept for a minute so the tooltip does not ask
/// again while the pointer stays there.
/// </summary>
public sealed class MarketFeed
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);
    private readonly FarmFeedClient _http = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Cached> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Cached
    {
        public FmMarket.Prices Prices { get; init; } = new();
        public DateTimeOffset At { get; init; }
        public bool Failed { get; init; }
    }

    public event Action? Updated;

    public (IReadOnlyList<long> Listings, IReadOnlyList<long> Sales, string? Status) Quote(string item)
    {
        var key = FmMarket.Key(item);
        if (key.Length == 0) return ([], [], null);
        Cached? cached;
        var start = false;
        lock (_gate)
        {
            _items.TryGetValue(key, out cached);
            var due = cached == null || DateTimeOffset.UtcNow - cached.At >= Fresh;
            if (due && _loading.Add(key)) start = true;
        }

        if (start) _ = Task.Run(() => Load(key));
        if (cached == null) return ([], [], "Loading the market…");
        if (cached.Failed) return ([], [], "Market unavailable");
        return (cached.Prices.Listings, cached.Prices.Sales, null);
    }

    private async Task Load(string key)
    {
        var failed = true;
        FmMarket.Prices? prices = null;
        try
        {
            var saved = WebsiteSettingsStore.Load();
            var feed = string.IsNullOrWhiteSpace(saved.Url) ? WebsiteAddress.LiveFeed : saved.Url;
            var (ok, _, body) = await _http.GetAsync(WebsiteAddress.FmItemUrl(feed, key), saved.Token);
            if (ok && body.Length > 0)
            {
                prices = FmMarket.ReadItem(body, key);
                failed = false;
            }
        }
        catch (Exception)
        {
            failed = true;
        }

        lock (_gate)
        {
            _loading.Remove(key);
            _items.TryGetValue(key, out var previous);
            if (failed && previous is { Failed: false })
            {
                _items[key] = new Cached { Prices = previous.Prices, At = DateTimeOffset.UtcNow, Failed = false };
            }
            else
            {
                _items[key] = new Cached
                {
                    Prices = prices ?? new FmMarket.Prices(),
                    At = DateTimeOffset.UtcNow,
                    Failed = failed,
                };
            }
        }

        Updated?.Invoke();
    }
}
