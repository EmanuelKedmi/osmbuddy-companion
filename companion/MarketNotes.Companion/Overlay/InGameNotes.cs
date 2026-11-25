using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// The notes panel and the item card. Both are separate windows on the
/// client, so a click on the game does not land on them. The card is a stack
/// of sections beside the client's own tooltip.
/// </summary>
public sealed class InGameNotes : IDisposable
{
    private const int HotkeyId = 0x4D4E;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkM = 0x4D;

    private readonly InGameUi _ui = new();
    private readonly HoverMarket _market;
    private readonly MarketFeed _feed = new();
    private readonly NotesPanelWindow _panel = new();
    private readonly HoverCardWindow _card = new();
    private readonly DispatcherTimer _timer;
    private HwndSource? _hook;
    private IntPtr _ownerHwnd;
    private ShareSource? _share;
    private bool _sharing;
    private bool _bound;
    private bool _placing;
    private bool _disposed;
    private PixelRect? _anchor;
    private PixelRect? _titleAt;
    private int? _titleHash;
    private int _inkScale = 1;
    private int _hitCursorX;
    private int _hitCursorY;
    private int _misses;
    private int _sampleId;
    private readonly NameHold _names = new();
    private string? _title;
    private string? _shownItem;

    public InGameNotes(HoverMarket? market = null)
    {
        _market = market ?? new HoverMarket();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _feed.Updated += () => _timer.Dispatcher.Invoke(RefreshMarketCard);
        _panel.CloseRequested += () => SetOpen(false);
        _panel.SizeChanged += (_, _) =>
        {
            if (_placing || _share == null || !_sharing || !_ui.Open) return;
            Place(_share);
        };
        _timer.Tick += (_, _) => RefreshHover();
    }

    public bool IsOpen => _ui.Open;

    /// <summary>A section is selected, so the card stays on after the menu closes.</summary>
    public bool IsActive => _ui.AnyEnabled;

    public event Action? OpenChanged;

    public event Action? ActivityChanged;

    public void Attach(Window owner)
    {
        owner.SourceInitialized += (_, _) =>
        {
            _ownerHwnd = new WindowInteropHelper(owner).Handle;
            _hook = HwndSource.FromHwnd(_ownerHwnd);
            _hook?.AddHook(WndProc);
            RegisterHotKey(_ownerHwnd, HotkeyId, ModControl | ModShift, VkM);
        };
    }

    public void SetOpen(bool open)
    {
        var changed = _ui.Open != open;
        _ui.SetOpen(open);
        Apply();
        if (changed) OpenChanged?.Invoke();
    }

    public void Toggle() => SetOpen(!_ui.Open);

    public void SetSharing(bool sharing)
    {
        _sharing = sharing;
        if (!sharing) ForgetTooltip();

        Apply();
    }

    public void Place(ShareSource source)
    {
        _share = source;
        if (!GameInFront())
        {
            HideCard();
            if (_panel.IsVisible) _panel.Hide();
            return;
        }

        if (!_sharing || !_ui.Open || _placing) return;
        if (!GameClientBounds.TryGet(source, out var client))
        {
            if (_panel.IsVisible) _panel.Hide();
            return;
        }

        _placing = true;
        try
        {
            ShowPanel();
            var (scaleX, scaleY) = OverlayChrome.Scale(_panel);
            var place = NotesLayout.Panel(
                client.Width,
                client.Height,
                (int)Math.Ceiling(_panel.Width * scaleX),
                (int)Math.Ceiling(_panel.BodyHeight * scaleY));
            _panel.MoveTo(client.X + place.X, client.Y + place.Y);
        }
        finally
        {
            _placing = false;
        }
    }

    public void NoteFrame(CapturedFrame frame)
    {
        // The scanners use this grab. The item card copies the pointer area on its own timer.
        _ = frame;
    }

    public void RememberShop(FmFrameInput shop, IReadOnlyList<SoldListing> sold, bool sample)
    {
        var place = HoverMarket.PlaceOf(shop.ShopName ?? "", shop.MapName ?? "");
        var owner = shop.Owner ?? "";
        var at = DateTimeOffset.UtcNow;
        foreach (var row in shop.Rows)
        {
            _market.ObserveShop(row.Item, owner, place, row.PriceEach, row.Quantity, at, sample);
        }

        foreach (var sale in sold)
        {
            _market.ObserveSale(sale.Item, sale.PriceEach, at, sample);
        }

        _market.Save();
    }

    public void CloseOwner(string owner)
    {
        _market.CloseOwner(owner);
        _market.Save();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        if (_ownerHwnd != IntPtr.Zero) UnregisterHotKey(_ownerHwnd, HotkeyId);
        _hook?.RemoveHook(WndProc);
        _panel.Close();
        _card.Close();
    }

    private void Apply()
    {
        if (_sharing && _ui.AnyEnabled)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
            HideCard();
        }

        if (_sharing && _ui.Open)
        {
            if (_share != null) Place(_share);
            else if (GameInFront()) ShowPanel();
            return;
        }

        if (_panel.IsVisible) _panel.Hide();
    }

    private void ShowPanel()
    {
        if (!_bound)
        {
            _panel.Bind(_ui, () =>
            {
                ActivityChanged?.Invoke();
                Apply();
                RefreshHover();
            });
            _bound = true;
        }

        if (!_panel.IsVisible) _panel.Show();
    }

    private void RefreshHover()
    {
        if (!_sharing || !_ui.AnyEnabled || _share == null || !GameInFront())
        {
            HideCard();
            if (_share != null && !GameInFront() && _panel.IsVisible) _panel.Hide();
            return;
        }

        if (_ui.Open && !_panel.IsVisible) Place(_share);

        if (!GameClientBounds.TryGet(_share, out var client)
            || !GameClientBounds.TryCursor(_share, client, out var cursorX, out var cursorY))
        {
            HideCard();
            return;
        }

        if (CursorOnCard()) return;

        if (_titleAt is { } held
            && Math.Abs(cursorX - _hitCursorX) + Math.Abs(cursorY - _hitCursorY) <= 24
            && _titleHash is int heldHash)
        {
            var title = HoverSample.Copy(_share, client, held.X, held.Y, held.Width, held.Height);
            if (title != null)
            {
                var again = TooltipTitle.Hash(title.Value.Frame.Bgra, title.Value.Frame.Width, title.Value.Frame.Height, title.Value.Frame.Width * 4, new PixelRect(0, 0, title.Value.Frame.Width, title.Value.Frame.Height));
                if (again == heldHash && _names.HasCandidate)
                {
                    _misses = 0;
                    ShowSettled(_names.Ready(again, ++_sampleId, () => null));
                    return;
                }
            }
        }

        var shot = HoverSample.Copy(_share, client, cursorX - 620, cursorY - 700, 1240, 1220);
        if (shot == null)
        {
            Miss();
            return;
        }

        var frame = shot.Value.Frame;
        var stride = frame.Width * 4;
        var found = ItemTooltipFinder.Find(frame.Bgra, frame.Width, frame.Height, stride, cursorX - shot.Value.OriginX, cursorY - shot.Value.OriginY);
        if (found == null)
        {
            Miss();
            return;
        }

        _misses = 0;
        _hitCursorX = cursorX;
        _hitCursorY = cursorY;
        _anchor = Shift(found.Value.Bounds, shot.Value.OriginX, shot.Value.OriginY);
        _titleAt = Shift(found.Value.Title, shot.Value.OriginX, shot.Value.OriginY);
        _inkScale = found.Value.Icon.Width < 96 ? 2 : 1;
        var hash = TooltipTitle.Hash(frame.Bgra, frame.Width, frame.Height, stride, found.Value.Title);
        _titleHash = hash;
        var titleRect = found.Value.Title;
        var scale = _inkScale;
        ShowSettled(_names.Ready(hash, ++_sampleId, () =>
        {
            if (!WindowsOcr.IsAvailable) return null;
            var ink = TooltipTitle.Ink(frame.Bgra, frame.Width, frame.Height, stride, titleRect, scale);
            return TooltipName.FromLines(WindowsOcr.ReadLines(ink.Bgra, ink.Width, ink.Height));
        }));
    }

    private void Miss()
    {
        if (++_misses <= 1) return;
        ForgetTooltip();
        Conceal();
        _panel.SetStatus("No item tooltip by the pointer.");
    }

    private void ShowSettled(string? name)
    {
        var seen = _names.Latest;
        if (!string.IsNullOrWhiteSpace(seen)) _feed.Quote(seen);
        if (string.IsNullOrWhiteSpace(name))
        {
            _title = null;
            Conceal();
            _panel.SetStatus(WindowsOcr.IsAvailable ? "Reading the name…" : WindowsOcr.Status);
            return;
        }

        _title = name;
        _panel.SetStatus(_title);
        Present(_title);
    }

    private static PixelRect Shift(PixelRect rect, int originX, int originY) =>
        new(rect.X + originX, rect.Y + originY, rect.Width, rect.Height);

    private bool GameInFront()
    {
        if (_share == null) return false;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (IsOverlay(foreground)) return true;

        if (_share.Kind == ShareKind.Window)
        {
            var game = _share.WindowHandle;
            if (!NativeMethods.IsWindow(game) || NativeMethods.IsIconic(game)) return false;
            return NativeMethods.GetAncestor(foreground, 2) == NativeMethods.GetAncestor(game, 2);
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;
        if (!NativeMethods.GetWindowRect(foreground, out var rect)) return false;
        var cx = rect.Left + (rect.Right - rect.Left) / 2;
        var cy = rect.Top + (rect.Bottom - rect.Top) / 2;
        return cx >= _share.X && cy >= _share.Y && cx < _share.X + _share.Width && cy < _share.Y + _share.Height;
    }

    private bool IsOverlay(IntPtr hwnd)
    {
        var panel = new WindowInteropHelper(_panel).Handle;
        var card = new WindowInteropHelper(_card).Handle;
        return hwnd != IntPtr.Zero && (hwnd == panel || hwnd == card);
    }

    private void ForgetTooltip()
    {
        _anchor = null;
        _titleAt = null;
        _titleHash = null;
        _misses = 0;
        _names.Reset();
        _title = null;
        _shownItem = null;
    }

    private void Present(string name)
    {
        if (_anchor is not { } anchor || _share == null) return;
        if (!GameClientBounds.TryGet(_share, out var client)) return;
        if (!_card.IsVisible || !string.Equals(_shownItem, name, StringComparison.Ordinal)) BindCard(name);
        if (!_card.IsVisible && _shownItem == null) return;

        var (scaleX, scaleY) = OverlayChrome.Scale(_card);
        var place = NotesLayout.BesideTooltip(
            anchor.X,
            anchor.Y,
            anchor.Width,
            anchor.Height,
            client.Width,
            client.Height,
            (int)Math.Ceiling(_card.Width * scaleX),
            (int)Math.Ceiling(_card.BodyHeight * scaleY));
        _card.MoveTo(client.X + place.X, client.Y + place.Y);
    }

    private void RefreshMarketCard()
    {
        if (_disposed || !_ui.AnyEnabled || !_sharing || !GameInFront() || _title == null || _anchor == null || _share == null) return;
        _shownItem = null;
        Present(_title);
    }

    private void BindCard(string name)
    {
        var quote = _feed.Quote(name);
        var read = new HoverRead
        {
            ItemName = name,
            Quote = PriceCatalog.Lookup(name),
            Trend = _market.Trend(name),
            ListingPrices = quote.Listings,
            SalePrices = quote.Sales,
            MarketStatus = quote.Status,
            Shops = _market.ActiveShops(name),
            Rolls = GearRolls.For(name),
        };
        var view = HoverSections.Compose(read, _ui.IsEnabled, _ui.IsExpanded);
        if (view == null)
        {
            _shownItem = null;
            Conceal();
            return;
        }

        _shownItem = name;
        _card.Show(view, id =>
        {
            _ui.ToggleExpanded(id);
            _shownItem = null;
            if (_title != null) Present(_title);
        });
    }

    private bool CursorOnCard()
    {
        if (!_card.IsVisible || _card.ActualWidth < 1 || _card.ActualHeight < 1) return false;
        if (!NativeMethods.GetCursorPos(out var point)) return false;
        var (scaleX, scaleY) = OverlayChrome.Scale(_card);
        var left = _card.Left * scaleX;
        var top = _card.Top * scaleY;
        var right = left + _card.ActualWidth * scaleX;
        var bottom = top + _card.ActualHeight * scaleY;
        return point.X >= left && point.X < right && point.Y >= top && point.Y < bottom;
    }

    private void HideCard() => Conceal();

    private void Conceal()
    {
        if (_card.IsVisible) _card.Hide();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
