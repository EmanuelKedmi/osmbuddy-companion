using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Launch;
using MarketNotes.Companion.Export;
using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Modes;
using MarketNotes.Companion.Models;
using MarketNotes.Companion.Overlay;
using MarketNotes.Companion.Scanning;
using Microsoft.Win32;

namespace MarketNotes.Companion;

public partial class MainWindow : Window
{
    private readonly IScreenCapture _capture = ScreenCaptureSelector.Create();
    private readonly DropNoticeWatch _drops = new();
    private string? _lootLogged;
    private long _lootSampledAt;
    private readonly BattleRegionWatch _battle = new();
    private readonly FarmFeedClient _feed = new();
    private readonly FmScanMachine _fm = new();
    private readonly FmLiveReader _fmLive = new();
    private readonly ListingSales _sales = new();
    private readonly TrainingSession _training = new();
    private readonly TrainingWatch _watch = new();
    private readonly TrainingIdentityReader _identity = new();
    private readonly SheetWindowWatch _sheet = new();
    private readonly GameOverlayWindow _overlay = new();
    private readonly LootPoolStore _pool = new();
    private DispatcherTimer? _shareTimer;
    private ShareSource? _source;
    private bool _sharing;
    private bool _uiReady;
    private bool _loadingSources;
    private int _quietTicks;
    private int _tickBusy;
    private ScannerMode _mode = ScannerMode.Training;
    private bool _settingMapBox;
    private bool _sample;
    private bool _trainingSample;
    private string _trainingReport = "";
    private List<ShopRowView> _rows = [];
    private IReadOnlyList<FmVisibleRow> _hidden = [];
    private string _hiddenKey = "";
    private int _shopFrameMisses;
    private string? _closeSentFor;
    private int _leftOut;
    private bool _loadingWebsite;
    private string _savedWebsiteUrl = "";
    private string _savedWebsiteToken = "";
    private string? _mirrorNote;
    private int _mirrorFailed;
    private long _nextMirrorAttempt;
    private string _statReport = "";
    private string _skillReport = "";
    private bool _characterSample;
    private string? _statSignature;
    private string? _statMirrorPending;
    private bool _statWindowOpen;
    private bool _statFeedSkipLogged;
    private string? _skillSignature;
    private string? _skillMirrorPending;
    private string? _statusSignature;
    private string? _statusMirrorPending;
    private IReadOnlyList<string> _ledgerNames = [];
    private long _ledgerNamesAt;
    private DispatcherTimer? _ngmWatch;
    private DispatcherTimer? _ngmRegister;
    private DispatcherTimer? _updateTimer;
    private int _ngmWatchTicks;
    private string? _ngmNote;
    private int _updateCheck;
    private const int StatFeedUiMaxLines = 80;

    public MainWindow()
    {
        InitializeComponent();
        var version = RunningVersion.Label();
        VersionButton.Content = version;
        Title = "Companion " + version;
        if (DebugLog.DevBuild) BuildSubtitle.Text = "Old School Maple · dev build, always logging";
        LoadWebsiteSettings();
        var debug = DebugUiStore.Load();
        BugButton.IsChecked = debug;
        ApplyDebug(debug, save: false);
        if (WindowsOcr.IsAvailable)
        {
            _settingMapBox = true;
            FarmMapNameBox.Text = "";
            _settingMapBox = false;
        }

        _uiReady = true;
        ApplyMode(ScannerModeChoice.ForMap(CurrentMapName()));
        StatusText.Text = "FM scanner is idle. " + WindowsOcr.Status;
        FarmStatus.Text = "Training scanner is idle.";
        CharacterStatus.Text = "Character scanner is idle.";
        SeenStateBox.Text = WindowsOcr.Status + "\nThe share is stopped.";
        StatFeedLog.LineWritten += OnStatFeedLine;
        Closed += (_, _) =>
        {
            StatFeedLog.LineWritten -= OnStatFeedLine;
            StopNgmWatch();
            _ngmRegister?.Stop();
            _updateTimer?.Stop();
            PersistWebsiteSettings();
            _overlay.Close();
        };
        if (NgmHijackPreference.IsEnabled()) StartNgmRegisterTimer();
        RefreshNgmHijack();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        _updateTimer.Start();
        LoadSources();
        _ = CheckForUpdateAsync();
        if (Environment.GetCommandLineArgs().Any(arg =>
                string.Equals(arg, "--share", StringComparison.OrdinalIgnoreCase)))
        {
            Loaded += (_, _) =>
            {
                if (SelectedSource() != null) StartShare_Click(this, new RoutedEventArgs());
            };
        }
    }

    private void Version_Click(object sender, RoutedEventArgs e)
    {
        var window = new ChangelogWindow { Owner = this };
        window.ShowDialog();
    }

    private void OnStatFeedLine(string line)
    {
        void Append()
        {
            if (StatFeedLogBox == null) return;
            var stamp = DateTimeOffset.Now.ToString("HH:mm:ss");
            var next = stamp + " " + line;
            var current = StatFeedLogBox.Text ?? "";
            if (current.StartsWith("Press S with share on.", StringComparison.Ordinal))
                current = "";
            var combined = string.IsNullOrEmpty(current) ? next : current + "\n" + next;
            var parts = combined.Split('\n');
            if (parts.Length > StatFeedUiMaxLines)
                combined = string.Join("\n", parts.AsSpan(parts.Length - StatFeedUiMaxLines).ToArray());
            StatFeedLogBox.Text = combined;
            StatFeedLogBox.CaretIndex = StatFeedLogBox.Text.Length;
            StatFeedLogBox.ScrollToEnd();
        }

        if (Dispatcher.CheckAccess()) Append();
        else _ = Dispatcher.BeginInvoke(Append);
    }

    private async Task CheckForUpdateAsync()
    {
        var ticket = Interlocked.Increment(ref _updateCheck);
        var newer = await CompanionUpdate.NewerVersionAsync(FeedUrlBox.Text, RunningVersion.Label());
        if (ticket != _updateCheck || UpdateButton == null) return;
        if (newer == null)
        {
            UpdateButton.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateButton.ToolTip = "Update to " + newer;
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        var staged = await CompanionUpdate.StageAsync(FeedUrlBox.Text);
        if (!staged.Ok)
        {
            StatusText.Text = staged.Message;
            UpdateButton.IsEnabled = true;
            return;
        }

        Application.Current.Shutdown();
    }

    private void StartNgmRegisterTimer()
    {
        if (_ngmRegister == null)
        {
            _ngmRegister = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _ngmRegister.Tick += (_, _) => NgmProtocol.EnsureRegistered();
        }

        _ngmRegister.Start();
    }

    private void NgmHijack_Click(object sender, RoutedEventArgs e)
    {
        if (NgmProtocol.IsInstalled())
        {
            if (!NgmProtocol.Uninstall())
            {
                RefreshNgmHijack("The ngm:// hijack could not be removed.");
                return;
            }

            NgmHijackPreference.SetEnabled(false);
            _ngmRegister?.Stop();
            RefreshNgmHijack();
            return;
        }

        if (!NgmProtocol.Install())
        {
            RefreshNgmHijack("NGM was not found, so the ngm:// hijack stays off.");
            return;
        }

        if (!NgmHijackPreference.SetEnabled(true))
        {
            NgmProtocol.Uninstall();
            RefreshNgmHijack("The ngm:// hijack could not be saved, so it stays off.");
            return;
        }

        StartNgmRegisterTimer();
        RefreshNgmHijack();
    }

    private void RefreshNgmHijack(string? note = null)
    {
        var installed = NgmProtocol.IsInstalled();
        NgmHijackButton.Content = installed ? "Uninstall ngm:// hijack" : "Install ngm:// hijack";
        NgmHijackStatus.Text = note ?? (installed
            ? "ngm:// hijack is on. Play opens this companion, then Nexon Game Manager."
            : "ngm:// hijack is off. Play opens Nexon Game Manager directly.");
    }

    /// <summary>
    /// Play started this process. The launch string was already handed to NGM
    /// or dropped. It is not kept here.
    /// </summary>
    public void OnNgmLaunch(bool handedToNgm)
    {
        _ngmNote = handedToNgm
            ? "Play opened the companion and handed that launch to NGM."
            : "Play opened the companion. NGM was not started.";
        ShareStatus.Text = _ngmNote;
        BringForward();
        ArmNgmShare();
    }

    public void OnNgmActivate()
    {
        _ngmNote = "Play asked the companion to watch the game.";
        ShareStatus.Text = _ngmNote;
        BringForward();
        if (!_sharing) ArmNgmShare();
    }

    private void BringForward()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void ArmNgmShare()
    {
        StopNgmWatch();
        _ngmWatchTicks = 0;
        TryNgmShare();
        if (_sharing) return;
        _ngmWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ngmWatch.Tick += (_, _) =>
        {
            _ngmWatchTicks++;
            TryNgmShare();
            if (_sharing || _ngmWatchTicks >= 60) StopNgmWatch();
        };
        _ngmWatch.Start();
    }

    private void StopNgmWatch()
    {
        if (_ngmWatch == null) return;
        _ngmWatch.Stop();
        _ngmWatch = null;
    }

    private void TryNgmShare()
    {
        if (_sharing || ScreenSourceButton.IsChecked == true) return;
        LoadSources();
        if (SourceList.SelectedItem is not SharedWindow window || !IsMswWindow(window)) return;
        StartShare_Click(this, new RoutedEventArgs());
    }

    private void MapName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _settingMapBox) return;
        if (_identity.ModeMapName.Length > 0)
        {
            SyncMapBox(_identity.ModeMapName);
            return;
        }

        if (_sharing) return;
        ApplyMode(ScannerModeChoice.ForMap(CurrentMapName()));
    }

    private void ApplyMode(ScannerMode mode)
    {
        var changed = _mode != mode;
        _mode = mode;
        if (FmPanel != null) FmPanel.Visibility = mode == ScannerMode.Fm ? Visibility.Visible : Visibility.Collapsed;
        if (TrainingPanel != null) TrainingPanel.Visibility = mode == ScannerMode.Training ? Visibility.Visible : Visibility.Collapsed;
        if (ModeStatus != null) ModeStatus.Text = ModeSentence(mode);
        if (!changed) return;
        if (mode != ScannerMode.Training)
        {
            var closed = _watch.Stop();
            if (closed != null)
            {
                MirrorSession(closed, true, "The map is a Free Market room. Training scanner stopped.");
            }
        }

        _drops.Reset();
        _battle.Reset();
        RenderOverlays();
        if (_sharing && _source != null && ShareStatus != null)
        {
            var active = mode == ScannerMode.Fm ? "FM scanner" : "Training scanner";
            var stopped = mode == ScannerMode.Fm ? "Training scanner" : "FM scanner";
            ShareStatus.Text = $"Sharing {_source.StatusLabel}. {stopped} stopped. {active} is watching this share because {MapBecause()}. Capture is {_capture.ImplementationName}.";
        }
    }

    private string ModeSentence(ScannerMode mode)
    {
        var scanner = mode == ScannerMode.Fm ? "FM scanner" : "Training scanner";
        var where = CurrentMapName();
        var map = string.IsNullOrWhiteSpace(where) ? "No map yet" : where;
        return $"{map}. {scanner} is on. The map is checked on every frame. A Free Market map runs the FM scanner. Any other map runs the Training scanner.";
    }

    private string MapBecause()
    {
        var where = CurrentMapName();
        return string.IsNullOrWhiteSpace(where) ? "no Free Market map has been read" : $"the map is {where}";
    }

    private string? CurrentMapName()
    {
        if (_identity.ModeMapName.Length > 0) return _identity.ModeMapName;
        return FallbackMapName();
    }

    private void SyncMapBox(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName) || FarmMapNameBox == null) return;
        if (string.Equals(FarmMapNameBox.Text.Trim(), mapName, StringComparison.Ordinal)) return;
        _settingMapBox = true;
        FarmMapNameBox.Text = mapName;
        _settingMapBox = false;
    }

    private void SourceKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        if (_sharing) StopShare("Share stopped because the source changed. FM scanner and Training scanner are stopped.");
        LoadSources();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var key = SourceKey(SourceList.SelectedItem);
        var wasSharing = _sharing;
        LoadSources();
        var previous = FindSource(key);
        if (wasSharing && previous != null)
        {
            _loadingSources = true;
            SourceList.SelectedItem = previous;
            _loadingSources = false;
            return;
        }

        if (SourceList.SelectedItem == null && previous != null)
        {
            _loadingSources = true;
            SourceList.SelectedItem = previous;
            _loadingSources = false;
        }

        if (wasSharing && previous == null)
        {
            StopShare("Share stopped. That source is gone. FM scanner and Training scanner are stopped.");
            return;
        }

        if (_sharing) return;
        var source = SelectedSource();
        if (source == null) return;
        SourceDescription.Text = $"{source.Description} Sharing has not started.";
        TryThumbnail(source);
    }

    private void LoadSources()
    {
        _loadingSources = true;
        try
        {
            if (ScreenSourceButton.IsChecked == true)
            {
                IReadOnlyList<SharedScreen> screens;
                try
                {
                    screens = ScreenEnumerator.Screens();
                }
                catch (Exception ex)
                {
                    SourceList.ItemsSource = null;
                    SourceDescription.Text = $"No screens could be listed ({ex.GetType().Name}). EnumDisplayMonitors needs Windows.";
                    return;
                }

                SourceList.ItemsSource = screens;
                SourceList.SelectedIndex = -1;
                SourceDescription.Text = screens.Count == 0
                    ? "No screens could be listed. The list is the monitor index and bounds from EnumDisplayMonitors, which needs Windows."
                    : "Choose a screen. Each row is the screen index and its bounds. Sharing has not started.";
                return;
            }

            IReadOnlyList<SharedWindow> windows;
            try
            {
                windows = WindowEnumerator.TopLevelWindows();
            }
            catch (Exception ex)
            {
                SourceList.ItemsSource = null;
                SourceDescription.Text = $"No windows could be listed ({ex.GetType().Name}).";
                return;
            }

            SourceList.ItemsSource = windows;
            SourceList.SelectedIndex = -1;
            var msw = windows.FirstOrDefault(IsMswWindow);
            if (msw != null)
            {
                SourceList.SelectedItem = msw;
                var source = SelectedSource();
                SourceDescription.Text = source == null
                    ? $"Selected {msw.Display}. Sharing has not started."
                    : $"{source.Description} Sharing has not started.";
                if (_uiReady && source != null) TryThumbnail(source);
            }
            else
            {
                SourceDescription.Text = windows.Count == 0
                    ? "No titled windows are open. Open the game, then refresh."
                    : "Choose a window. Each row is the window title and the process. Sharing has not started.";
            }
        }
        finally
        {
            _loadingSources = false;
        }
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _loadingSources) return;
        if (_sharing) StopShare("Share stopped. Start the share again for the new source. FM scanner and Training scanner are stopped.");
        var source = SelectedSource();
        if (source == null) return;
        SourceDescription.Text = $"{source.Description} Sharing has not started.";
        TryThumbnail(source);
    }

    private void StartShare_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedSource();
        if (source == null)
        {
            ShareStatus.Text = ScreenSourceButton.IsChecked == true
                ? "Choose a screen before starting the share."
                : "Choose a window before starting the share.";
            return;
        }

        _source = source;
        _sharing = true;
        _quietTicks = 0;
        ResetFmScan(clearTable: true);
        StartShareButton.IsEnabled = false;
        StopShareButton.IsEnabled = true;
        var scanner = _mode == ScannerMode.Fm ? "FM scanner" : "Training scanner";
        ShareStatus.Text = $"Sharing {source.StatusLabel}. Capture is {_capture.ImplementationName}. {scanner} is on because {MapBecause()}. {WindowsOcr.Status}";
        if (_ngmNote != null)
        {
            ShareStatus.Text = _ngmNote + " " + ShareStatus.Text;
            _ngmNote = null;
        }
        _overlay.Place(source);
        SourceDescription.Text = source.Description;
        _shareTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _shareTimer.Tick += (_, _) => ShareTick();
        _shareTimer.Start();
        ShareTick();
    }

    private void StopShare_Click(object sender, RoutedEventArgs e)
    {
        StopShare("Share stopped. FM scanner and Training scanner are stopped.");
    }

    private void StopShare(string status)
    {
        _sharing = false;
        if (_shareTimer != null)
        {
            _shareTimer.Stop();
            _shareTimer = null;
        }

        _source = null;
        _drops.Reset();
        _battle.Reset();
        _identity.Reset();
        _sheet.Reset();
        ResetFmScan(clearTable: false);
        _overlay.CloseOverlay();
        if (StartShareButton != null) StartShareButton.IsEnabled = true;
        if (StopShareButton != null) StopShareButton.IsEnabled = false;
        if (ShareStatus != null) ShareStatus.Text = status;
    }

    private void ResetFmScan(bool clearTable)
    {
        _fm.Reset();
        _fmLive.Reset();
        _sales.Reset();
        _shopFrameMisses = 0;
        _closeSentFor = null;
        _leftOut = 0;
        _hidden = [];
        _hiddenKey = "";
        if (clearTable) ShowLocalShop(sample: false);
    }

    private void ShareTick()
    {
        if (!_sharing || _source == null) return;
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1) return;
        try
        {
            CapturedFrame? frame;
            try
            {
                frame = _capture.Grab(_source);
            }
            catch (Exception ex)
            {
                ShareStatus.Text = $"Sharing {_source.StatusLabel}. The grab failed ({ex.GetType().Name}).";
                return;
            }

            if (frame == null)
            {
                if (_sharing) ShareStatus.Text = $"Sharing {_source.StatusLabel}. The grab returned no pixels.";
                return;
            }

            if (!_sharing || _source == null) return;
            ShowFrame(frame);
            _overlay.Place(_source);
            _identity.ReadMap(frame);
            if (_identity.ModeMapName.Length > 0) SyncMapBox(_identity.ModeMapName);
            ApplyMode(ScannerModeChoice.ForMap(CurrentMapName()));
            if (_mode == ScannerMode.Fm) FmLive(frame);
            else TrainingLive(frame);
            CharacterLive(frame);
        }
        finally
        {
            Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    private void FmLive(CapturedFrame frame)
    {
        var view = _fmLive.Read(frame);
        var detail = view.Detail;
        var hiddenKey = string.Join("\n", view.Hidden.Select(row => $"{row.Item}|{row.Quantity}|{row.PriceEach}"));
        var hiddenChanged = hiddenKey != _hiddenKey;
        _hiddenKey = hiddenKey;
        _hidden = view.Hidden;
        _leftOut = view.LeftOut;
        if (view.Shop != null && view.Shop.Rows.Count > 0)
        {
            _shopFrameMisses = 0;
            _closeSentFor = null;
            var sold = _sales.Observe(
                view.Shop.Owner ?? "",
                view.Shop.ShopName ?? "",
                view.Shop.MapName ?? "",
                view.Shop.Rows);
            var step = _fm.Absorb(view.Shop);
            ShowLocalShop(sample: false);
            detail += "\n" + step.Message;
            if (view.LeftOut > 0)
            {
                detail += view.LeftOut == 1
                    ? "\n1 row was left out of the shop list (unreadable or junk)."
                    : $"\n{view.LeftOut} rows were left out of the shop list (unreadable or junk).";
            }

            if (sold.Count > 0)
            {
                detail += sold.Count == 1
                    ? $"\nSold {sold[0].Item} for {sold[0].PriceEach:N0}."
                    : $"\nSold {sold.Count} listings.";
            }

            if (view.ListingsChanged || step.Added.Count > 0 || step.Reset || sold.Count > 0 || ShopMirrorDue())
            {
                UploadShop(sold);
            }
        }
        else if (!view.ShopFrameFound)
        {
            _shopFrameMisses++;
            if (_shopFrameMisses >= 8 && _fm.Shop != null)
            {
                var owner = _fm.Shop.Owner;
                if (_closeSentFor != owner)
                {
                    _closeSentFor = owner;
                    UploadClose(owner);
                    detail += $"\n{owner}'s shop left the screen. It stays on the site as closed, and its listings are delisted.";
                }

                _fm.Reset();
                _fmLive.ForgetListings();
                _sales.Reset();
                _leftOut = 0;
                _hidden = [];
                _hiddenKey = "";
                ShowLocalShop(sample: false);
            }
        }
        else
        {
            _shopFrameMisses = 0;
            if (_fm.Shop != null || hiddenChanged || view.LeftOut > 0)
            {
                ShowLocalShop(sample: false);
            }

            if (view.ShopFrameFound && (_fm.Shop == null || _fm.Shop.Lines.Count == 0))
            {
                detail += view.LeftOut > 0
                    ? $"\nShop is open, but no priced rows made the list. Left out {view.LeftOut}."
                    : "\nShop is open, but no priced rows were read yet.";
            }
        }

        if (_sample && _rows.Count > 0 && (view.Shop == null || view.Shop.Rows.Count == 0))
        {
            detail += "\nThe shop list below is still the sample.";
        }

        if (_mirrorNote != null) detail += "\n" + _mirrorNote;
        StatusText.Text = detail;
        var seen = $"FM scanner\nSharing {_source!.StatusLabel}\nFrame {frame.Width}×{frame.Height}\n{detail}";
        PublishSeen(seen, ToRegions(frame, view.Regions));
        ShareStatus.Text = ShareLine(FirstLine(detail));
    }

    private void TrainingLive(CapturedFrame frame)
    {
        _identity.Read(frame);
        _drops.Run(_capture, _source!);
        var loot = _drops.Drain();
        var reads = loot.Reads.Where(read => read.RegionChanged).ToList();
        var drops = reads.LastOrDefault() ?? new DropWatchResult
        {
            Status = loot.Status,
            RegionChanged = false,
            CaptureOk = loot.CaptureOk,
        };
        var battle = _battle.Inspect(frame);
        _drops.Cover = battle.Window;
        ShowIsolation(battle, drops);
        var changed = reads.Count > 0 || battle.RegionChanged;
        var local = !changed && battle.Window != null
            ? "Battle analysis and the loot corner are unchanged. Skipped reading. " + loot.Status
            : $"{battle.Status} {loot.Status} {drops.Status}";
        var who = _identity.CharacterName.Length > 0 ? _identity.CharacterName : null;
        var where = _identity.MapName.Length > 0 ? _identity.MapName : FallbackMapName();
        var mapId = _identity.MapName.Length > 0 ? _identity.MapId : FallbackMapId(where);
        BattleSnapshot? snapshot = battle.RegionChanged && battle.Lines.Count > 0
            ? BattleText.Parse(string.Join("\n", battle.Lines))
            : null;
        var frames = reads
            .Where(read => read.Lines.Count > 0 || read.Tops != null)
            .Select(read => new NoticeFrame
            {
                RegionHash = read.RegionHash,
                Lines = read.Lines.ToList(),
                Tops = read.Tops?.ToList(),
                CropHeight = read.CropHeight,
                AtMs = read.Tops != null ? read.AtMs : null,
            })
            .ToList();
        IReadOnlyList<NoticeFrame>? notices = frames.Count > 0 ? frames : null;
        var update = _watch.Observe(new TrainingSignal
        {
            MapName = where,
            MapId = mapId,
            CharacterName = who,
            Analysis = BattlePhase(battle),
            Battle = snapshot,
            Notices = notices,
            AtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        TrainingSessionLog.Append(_watch.DrainTrace());
        if (!battle.RegionChanged)
        {
            var whereBox = battle.Window is { } box
                ? $" box={box.X},{box.Y} {box.Width}x{box.Height} {battle.Button}"
                : "";
            BattleDebugLog.Skipped(battle.Status + whereBox);
        }
        else
        {
            var shownNow = update.Shown;
            var map = shownNow != null && shownNow.MapName.Length > 0 ? shownNow.MapName : where;
            BattleDebugLog.Read(
                battle.Lines,
                snapshot,
                map,
                shownNow?.Kills,
                snapshot == null ? "ignored" : _watch.BattleKillUse,
                battle.FromColumns);
        }

        var shown = update.Shown;
        if (shown != null)
        {
            _trainingSample = false;
            ShowSession(shown);
        }

        var identity = IdentityLine();
        foreach (var read in reads)
        {
            var text = string.Join(" | ", read.Lines);
            if (text == _lootLogged) continue;
            _lootLogged = text;
            LootOcrLog.Append(
                frame.Width,
                frame.Height,
                read.CropWidth,
                read.CropHeight,
                read.RegionHash,
                where,
                mapId,
                read.Lines,
                read.Words,
                read.Tops,
                read.AtMs);
            if (read.Isolated != null && read.AtMs - _lootSampledAt >= 300)
            {
                _lootSampledAt = read.AtMs;
                InkSampleDump.Save("loot", read.Isolated, read.IsolatedWidth, read.IsolatedHeight, text);
            }
        }

        var chrome = _identity.ChromeNote.Length > 0 ? "\n" + _identity.ChromeNote : "";
        if (_mirrorNote != null) local += "\n" + _mirrorNote;
        FarmStatus.Text = identity + "\n" + local + chrome;
        var seen = $"Training scanner\nSharing {_source!.StatusLabel}\nFrame {frame.Width}×{frame.Height}\n{identity}\n{local}{chrome}";
        if (_trainingReport.Length > 0) seen += "\n" + _trainingReport;
        PublishSeen(seen, TrainingRegions(frame, battle));
        ShareStatus.Text = ShareLine(FirstLine(identity));
        var quiet = drops.CaptureOk && battle.CaptureOk && !changed && update.JustClosed == null;
        if (quiet) _quietTicks++;
        else _quietTicks = 0;
        var due = changed || !drops.CaptureOk || !battle.CaptureOk || update.JustClosed != null || _quietTicks >= 8;
        if (!due) return;
        if (quiet) _quietTicks = 0;

        if (update.JustClosed != null && update.JustClosed != shown)
        {
            MirrorSession(update.JustClosed, drops.CaptureOk && battle.CaptureOk, local);
        }

        if (shown != null) MirrorSession(shown, drops.CaptureOk && battle.CaptureOk, local);
    }

    private static AnalysisSignal BattlePhase(BattleWatchResult battle)
    {
        if (battle.Held) return AnalysisSignal.Uncertain;
        if (battle.Button == BattleButton.Cancel) return AnalysisSignal.Running;
        if (battle.Button == BattleButton.Start) return AnalysisSignal.Idle;
        if (battle.Lost) return AnalysisSignal.Absent;
        return AnalysisSignal.Uncertain;
    }

    private string? FallbackMapName()
    {
        if (WindowsOcr.IsAvailable) return null;
        return string.IsNullOrWhiteSpace(FarmMapNameBox.Text) ? null : FarmMapNameBox.Text.Trim();
    }

    private int? FallbackMapId(string? mapName)
    {
        if (mapName == null) return null;
        if (!string.Equals(mapName, FarmMapNameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(FarmMapIdBox.Text.Trim(), out var parsed) && parsed > 0 ? parsed : null;
    }

    private string IdentityLine()
    {
        var who = _identity.CharacterName.Length > 0 ? _identity.CharacterName : "Character name not read yet";
        var where = _identity.MapName.Length > 0 ? _identity.MapName : "Map name not read yet";
        return $"{who} · {where}";
    }

    private void ShowSession(TrainingSession session)
    {
        if (session.CharacterName.Length > 0 && CharacterNameBox.Text.Trim() != session.CharacterName)
        {
            CharacterNameBox.Text = session.CharacterName;
        }

        if (session.MapName.Length > 0 && FarmMapNameBox.Text.Trim() != session.MapName)
        {
            FarmMapNameBox.Text = session.MapName;
        }

        if (session.MapId is int mapId)
        {
            var text = mapId.ToString(CultureInfo.InvariantCulture);
            if (FarmMapIdBox.Text.Trim() != text) FarmMapIdBox.Text = text;
        }

        var pool = LootPoolSnapshot.FromSession(session);
        _trainingReport = TrainingReport.Format(session, pool);
        if (TrainingOverlayText != null) TrainingOverlayText.Text = _trainingReport;
        if (TrainingOverlay != null)
        {
            TrainingOverlay.Visibility = _trainingReport.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        FillDropGrid(session);
    }

    private void FillDropGrid(TrainingSession session)
    {
        var who = string.IsNullOrWhiteSpace(session.CharacterName) ? CharacterIdentity().Name : session.CharacterName;
        LootPoolSnapshot total;
        try
        {
            total = _pool.Combined(who, session.MapId, session);
        }
        catch (Exception)
        {
            total = LootPoolSnapshot.FromSession(session);
        }

        TrainingKills.Text = session.MapId == null
            ? $"{session.Kills.ToString("N0", CultureInfo.InvariantCulture)} kills this session"
            : $"{session.Kills.ToString("N0", CultureInfo.InvariantCulture)} kills this session · {total.Kills.ToString("N0", CultureInfo.InvariantCulture)} total for {who} on this map";
        DropGrid.ItemsSource = session.Drops
            .OrderByDescending(row => row.Quantity)
            .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .Select(row =>
            {
                var totalQuantity = total.Quantity(row.ItemName);
                return new DropCountRow
                {
                    Item = row.ItemName,
                    Count = row.Quantity,
                    SessionRate = KillsPerDrop.Format(row.Quantity, session.Kills),
                    TotalRate = KillsPerDrop.Format(totalQuantity, total.Kills),
                    SessionKillsPerDrop = KillsPerDrop.Value(row.Quantity, session.Kills) ?? double.PositiveInfinity,
                    TotalKillsPerDrop = KillsPerDrop.Value(totalQuantity, total.Kills) ?? double.PositiveInfinity,
                };
            })
            .ToList();
    }

    private void MirrorSession(TrainingSession session, bool ok, string local)
    {
        try
        {
            _pool.ReplaceSession(session);
        }
        catch (Exception)
        {
            // The website still receives the visit when the local table cannot be written.
        }

        var (characterId, fallbackName) = CharacterIdentity();
        var characterName = string.IsNullOrWhiteSpace(session.CharacterName) ? fallbackName : session.CharacterName;
        MirrorLater(FeedUrlBox.Text, new
        {
            sessionId = session.SessionId(),
            mode = "replace",
            sitting = true,
            open = session.Open,
            characterId,
            characterName,
            mapId = session.MapId,
            mapName = string.IsNullOrWhiteSpace(session.MapName) ? "Unknown map" : session.MapName,
            kills = session.Kills,
            durationMs = session.DurationMs,
            source = "companion",
            ocr = WindowsOcr.IsAvailable,
            drops = session.Drops.Select(row => new { itemName = row.ItemName, quantity = row.Quantity }).ToArray(),
            battle = new
            {
                kills = session.Kills,
                durationMs = session.DurationMs,
                exp = session.Exp,
                damage = session.Damage,
                attacks = session.Attacks,
                meso = session.Meso,
            },
            heartbeat = new { ok, message = local },
        });
    }

    private void PublishSeen(string text, IReadOnlyList<SeenRegion> regions)
    {
        SeenStateBox.Text = text;
        _overlay.ShowSeen(text, regions);
    }

    private static string FirstLine(string text)
    {
        var split = text.Split('\n', 2);
        return split[0];
    }

    private string ShareLine(string detail)
    {
        var line = $"Sharing {_source!.StatusLabel}. Capture is {_capture.ImplementationName}. {detail}";
        return _mirrorNote == null ? line : $"{line} {_mirrorNote}";
    }

    private static SeenRegion Fraction(
        CapturedFrame frame,
        string name,
        PixelRect? box,
        double left,
        double top,
        double right,
        double bottom)
    {
        if (box is { IsEmpty: false } rect && frame.Width > 0 && frame.Height > 0)
        {
            return new SeenRegion(
                name,
                rect.X / (double)frame.Width,
                rect.Y / (double)frame.Height,
                rect.Width / (double)frame.Width,
                rect.Height / (double)frame.Height);
        }

        return new SeenRegion(name, left, top, right - left, bottom - top);
    }

    private static IReadOnlyList<SeenRegion> ToRegions(CapturedFrame frame, IReadOnlyList<FoundRegion> regions)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return [];
        return regions.Select(region => new SeenRegion(
            region.Name,
            region.X / (double)frame.Width,
            region.Y / (double)frame.Height,
            region.Width / (double)frame.Width,
            region.Height / (double)frame.Height)).ToList();
    }

    private IReadOnlyList<SeenRegion> TrainingRegions(CapturedFrame frame, BattleWatchResult battle)
    {
        var regions = new List<SeenRegion>
        {
            Fraction(frame, "Loot notices", _identity.LootBand, DropRegion.LeftFraction, DropRegion.TopFraction, DropRegion.RightFraction, DropRegion.BottomFraction),
            Fraction(frame, "Character name", _identity.NameBand, CharacterNameRegion.Left, CharacterNameRegion.Top, CharacterNameRegion.Right, CharacterNameRegion.Bottom),
        };
        var nameAt = 0;
        if (frame.Width > 0 && frame.Height > 0 && _identity.Minimap is { } minimap && !minimap.IsEmpty)
        {
            regions.Insert(0, new SeenRegion(
                "Minimap",
                minimap.X / (double)frame.Width,
                minimap.Y / (double)frame.Height,
                minimap.Width / (double)frame.Width,
                minimap.Height / (double)frame.Height));
            nameAt = 1;
        }

        if (frame.Width > 0 && frame.Height > 0 && _identity.MapBand is { } title && !title.IsEmpty)
        {
            regions.Insert(nameAt, new SeenRegion(
                "Map name",
                title.X / (double)frame.Width,
                title.Y / (double)frame.Height,
                title.Width / (double)frame.Width,
                title.Height / (double)frame.Height));
        }

        if (battle.Window is { } window && frame.Width > 0 && frame.Height > 0)
        {
            regions.Insert(0, new SeenRegion(
                "Battle analysis",
                window.X / (double)frame.Width,
                window.Y / (double)frame.Height,
                window.Width / (double)frame.Width,
                window.Height / (double)frame.Height));
        }

        return regions;
    }

    private void UploadShop(IReadOnlyList<SoldListing> sold)
    {
        var shop = _fm.Shop;
        if (shop == null || shop.Lines.Count == 0) return;
        MirrorLater(FmUrl(), new
        {
            source = "companion",
            ocr = true,
            sample = false,
            reset = true,
            frames = new[]
            {
                new
                {
                    mapName = shop.MapName,
                    owner = shop.Owner,
                    shopName = shop.ShopName,
                    channel = shop.Channel,
                    scrollbar = new
                    {
                        position = shop.Scrollbar.Position,
                        first = 0,
                        visible = Math.Max(1, shop.Lines.Count),
                    },
                    rows = shop.Lines.Select(line => new
                    {
                        item = line.Item,
                        quantity = line.Quantity,
                        priceEach = line.PriceEach,
                        slot = line.Slot,
                        row = line.Row,
                    }).ToArray(),
                },
            },
            sold = sold.Select(row => new
            {
                item = row.Item,
                quantity = row.Quantity,
                priceEach = row.PriceEach,
                owner = row.Owner,
                shopName = row.ShopName,
                mapName = row.MapName,
            }).ToArray(),
        });
    }

    private void UploadClose(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return;
        MirrorLater(FmUrl(), new
        {
            source = "companion",
            ocr = true,
            sample = false,
            reset = false,
            frames = Array.Empty<object>(),
            closed = new[] { new { owner } },
        });
    }

    private void TryThumbnail(ShareSource source)
    {
        try
        {
            var frame = _capture.Grab(source);
            if (frame == null)
            {
                PreviewImage.Source = null;
                PreviewHint.Visibility = Visibility.Visible;
                PreviewHint.Text = "No thumbnail for that source yet. The description above names it. Sharing has not started.";
                return;
            }

            ShowFrame(frame);
        }
        catch (Exception ex)
        {
            PreviewImage.Source = null;
            PreviewHint.Visibility = Visibility.Visible;
            PreviewHint.Text = $"No thumbnail ({ex.GetType().Name}). The description above names the source. Sharing has not started.";
        }
    }

    private void ShowFrame(CapturedFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length == 0 || frame.Bgra.Length % frame.Height != 0) return;
        var stride = frame.Bgra.Length / frame.Height;
        if (stride < frame.Width * 4) return;
        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.Bgra,
            stride);
        source.Freeze();
        PreviewImage.Source = source;
        PreviewHint.Visibility = Visibility.Collapsed;
    }

    private BitmapSource? _battleCropPreview;
    private BitmapSource? _lootCropPreview;
    private string? _expandedCrop;

    private void ShowIsolation(BattleWatchResult battle, DropWatchResult drops)
    {
        if (battle.RegionChanged && battle.Isolated != null)
        {
            var image = InkImage(battle.Isolated, battle.IsolatedWidth, battle.IsolatedHeight);
            if (image != null)
            {
                _battleCropPreview = image;
                BattleIsolationImage.Source = image;
                DebugBattleIsolationImage.Source = image;
                BattleIsolationHint.Visibility = Visibility.Collapsed;
                DebugBattleIsolationHint.Visibility = Visibility.Collapsed;
                if (BattleEnlargeButton != null) BattleEnlargeButton.IsEnabled = true;
                IsolationPreviewFiles.WriteBattle(battle.Isolated, battle.IsolatedWidth, battle.IsolatedHeight);
                if (_expandedCrop == "battle") PresentCrop("battle", image, dark: false);
            }

            FillValueCells(BattleValueCells, battle.ValueCells, captions: false);
            FillValueCells(DebugBattleValueCells, battle.ValueCells, captions: true);
        }

        if (drops.RegionChanged && drops.Isolated != null)
        {
            var image = InkImage(drops.Isolated, drops.IsolatedWidth, drops.IsolatedHeight);
            if (image != null)
            {
                _lootCropPreview = image;
                LootIsolationImage.Source = image;
                DebugLootIsolationImage.Source = image;
                LootIsolationHint.Visibility = Visibility.Collapsed;
                DebugLootIsolationHint.Visibility = Visibility.Collapsed;
                if (LootEnlargeButton != null) LootEnlargeButton.IsEnabled = true;
                IsolationPreviewFiles.WriteLoot(drops.Isolated, drops.IsolatedWidth, drops.IsolatedHeight);
                if (_expandedCrop == "loot") PresentCrop("loot", image, dark: true);
            }
        }
    }

    private void BattleEnlarge_Click(object sender, RoutedEventArgs e) => ToggleCrop("battle", _battleCropPreview, dark: false);

    private void LootEnlarge_Click(object sender, RoutedEventArgs e) => ToggleCrop("loot", _lootCropPreview, dark: true);

    private void CloseCropExpand_Click(object sender, RoutedEventArgs e) => CloseCropExpand();

    private void ToggleCrop(string key, BitmapSource? source, bool dark)
    {
        if (source == null) return;
        if (CropExpandHost.Visibility == Visibility.Visible && _expandedCrop == key)
        {
            CloseCropExpand();
            return;
        }

        PresentCrop(key, source, dark);
    }

    /// <summary>
    /// Shows one isolated crop, scaled by whole pixels so the ink stays sharp.
    /// The bitmap is the battle panel or the loot corner, never the grabbed frame.
    /// </summary>
    private void PresentCrop(string key, BitmapSource source, bool dark)
    {
        _expandedCrop = key;
        var label = key switch
        {
            "battle" => "Battle crop",
            "loot" => "Loot crop",
            _ => key.StartsWith("cell:", StringComparison.Ordinal) ? key["cell:".Length..] + " cell" : "Crop",
        };
        CropExpandTitle.Text = $"{label} · {source.PixelWidth}×{source.PixelHeight}";
        CropExpandFrame.Background = dark ? LootPreviewBrush : Brushes.White;
        var maxW = Math.Max(280, ActualWidth - 80);
        const double maxH = 480;
        CropExpandScroll.MaxWidth = maxW;
        var scale = FitCropScale(source.PixelWidth, source.PixelHeight, maxW, maxH);
        var width = Math.Max(1, source.PixelWidth * scale);
        var height = Math.Max(1, source.PixelHeight * scale);
        CropExpandFrame.Width = width;
        CropExpandFrame.Height = height;
        CropExpandImage.Width = width;
        CropExpandImage.Height = height;
        CropExpandImage.Source = source;
        CropExpandHost.Visibility = Visibility.Visible;
    }

    private static double FitCropScale(int pixelWidth, int pixelHeight, double maxW, double maxH)
    {
        for (var scale = 4; scale >= 1; scale--)
        {
            if (pixelWidth * scale <= maxW && pixelHeight * scale <= maxH) return scale;
        }

        // A wide loot strip can grow to 2× and scroll sideways. Height stays on screen.
        if (pixelHeight * 2 <= maxH && pixelWidth * 2 <= maxW * 2) return 2;

        var fit = Math.Min(maxW / pixelWidth, maxH / pixelHeight);
        return fit > 0 ? fit : 1;
    }

    private void CloseCropExpand()
    {
        _expandedCrop = null;
        CropExpandHost.Visibility = Visibility.Collapsed;
        CropExpandImage.Source = null;
    }

    private void FillValueCells(Panel host, IReadOnlyList<BattleColumnCrop> cells, bool captions)
    {
        host.Children.Clear();
        foreach (var cell in cells)
        {
            var image = InkImage(cell.Pixels, cell.Width, cell.Height);
            if (image == null) continue;
            var border = new Border
            {
                Width = captions ? 96 : 72,
                Height = captions ? 36 : 28,
                Margin = new Thickness(0, 0, 6, captions ? 4 : 0),
                Background = Brushes.White,
                BorderBrush = CellBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                ToolTip = cell.Name + " value. Click to enlarge this cell.",
                Child = new Image
                {
                    Source = image,
                    Stretch = Stretch.Uniform,
                },
            };
            RenderOptions.SetBitmapScalingMode(border.Child, BitmapScalingMode.NearestNeighbor);
            var cellKey = "cell:" + cell.Name;
            var cellImage = image;
            border.MouseLeftButtonUp += (_, _) => ToggleCrop(cellKey, cellImage, dark: false);
            if (!captions)
            {
                host.Children.Add(border);
                continue;
            }

            var stack = new StackPanel { Width = 96, Margin = new Thickness(0, 0, 6, 4) };
            stack.Children.Add(new TextBlock
            {
                Text = cell.Name,
                FontSize = 10,
                Foreground = CellLabelBrush,
            });
            border.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(border);
            host.Children.Add(stack);
        }

        host.Visibility = host.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static readonly SolidColorBrush CellLabelBrush = FreezeBrush(0x6B, 0x53, 0x44);
    private static readonly SolidColorBrush CellBorderBrush = FreezeBrush(0xE4, 0xD3, 0xB8);
    private static readonly SolidColorBrush LootPreviewBrush = FreezeBrush(0x24, 0x19, 0x10);

    private static SolidColorBrush FreezeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static BitmapSource? InkImage(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4) return null;
        try
        {
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            source.Freeze();
            return source;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private ShareSource? SelectedSource()
    {
        if (ScreenSourceButton.IsChecked == true)
        {
            if (SourceList.SelectedItem is not SharedScreen screen) return null;
            return new ShareSource
            {
                Kind = ShareKind.Screen,
                ScreenIndex = screen.Index,
                Title = screen.Display,
                X = screen.X,
                Y = screen.Y,
                Width = screen.Width,
                Height = screen.Height,
            };
        }

        if (SourceList.SelectedItem is not SharedWindow window) return null;
        return new ShareSource
        {
            Kind = ShareKind.Window,
            WindowHandle = window.Handle,
            Title = window.Title,
            ProcessName = window.ProcessName,
        };
    }

    private static string? SourceKey(object? item)
    {
        if (item is SharedWindow window) return "w:" + window.Handle.ToInt64();
        if (item is SharedScreen screen) return "s:" + screen.Index;
        return null;
    }

    private object? FindSource(string? key)
    {
        if (key == null) return null;
        foreach (var item in SourceList.Items)
        {
            if (SourceKey(item) == key) return item;
        }

        return null;
    }

    private static bool IsMswWindow(SharedWindow window)
    {
        return window.ProcessName.Contains("msw", StringComparison.OrdinalIgnoreCase)
            || window.Title.Contains("msw", StringComparison.OrdinalIgnoreCase);
    }

    private async void LoadFmSample_Click(object sender, RoutedEventArgs e)
    {
        List<FmFrameInput> frames;
        try
        {
            frames = FmFixture.LoadFrames();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"The FM sample could not be opened ({ex.Message}).";
            return;
        }

        _fm.Reset();
        foreach (var frame in frames) _fm.Step(frame);
        ShowLocalShop(sample: true);
        var local = "Sample shop is on this screen. Sample Seller, Sample Scroll Shelf, Free Market<1>. These rows were not read from a window. OCR did not run.";
        if (PriceCatalog.LoadWarning != null) local += " " + PriceCatalog.LoadWarning;
        StatusText.Text = local;

        var mirror = await MirrorAsync(FmUrl(), new
        {
            source = "fixture",
            ocr = false,
            sample = true,
            reset = true,
            frames,
        });
        if (!mirror.Attempted) return;
        StatusText.Text = local + (mirror.Ok
            ? " A copy was sent to the website."
            : " The website is closed, so no copy was sent. This shop stays on the screen.");
    }

    private void ShowLocalShop(bool sample)
    {
        var shop = _fm.Shop;
        _sample = sample;
        _rows = [];
        if (shop != null)
        {
            foreach (var line in shop.Lines)
            {
                var quote = PriceCatalog.Lookup(line.Item);
                _rows.Add(new ShopRowView
                {
                    Item = line.Item,
                    Quantity = line.Quantity,
                    PriceEach = line.PriceEach,
                    Slot = line.Slot,
                    DbMeso = quote.Meso,
                    DbPrice = quote.Meso?.ToString("N0", CultureInfo.InvariantCulture) ?? "—",
                    DbLabel = quote.Label,
                    Gap = PriceCatalog.GapText(line.PriceEach, quote),
                    Sold = line.Sold,
                });
            }
        }

        if (DebugLog.Panel && !sample)
        {
            foreach (var row in _hidden)
            {
                _rows.Add(new ShopRowView
                {
                    Item = row.Item,
                    Quantity = row.Quantity,
                    PriceEach = row.PriceEach,
                    DbPrice = "—",
                    DbLabel = "Debug only",
                    Gap = "Left out of the shop list",
                    Sold = false,
                    DebugOnly = true,
                });
            }
        }

        LinesGrid.ItemsSource = null;
        LinesGrid.ItemsSource = _rows;
        ExportButton.IsEnabled = _rows.Count > 0;
        SampleBanner.Visibility = sample ? Visibility.Visible : Visibility.Collapsed;
        ShopStateText.Text = shop == null
            ? string.Join(" ", _fm.Log.TakeLast(4))
            : ShopSummary(
                shop.Owner,
                shop.ShopName,
                shop.MapName,
                shop.Channel,
                shop.Scrollbar.Position,
                shop.Scrollbar.First,
                shop.Scrollbar.Visible,
                shop.Lines.Count,
                _leftOut);
        RenderOverlays();
    }

    private static string ShopSummary(
        string owner,
        string shop,
        string map,
        int? channel,
        double position,
        int first,
        int visible,
        int listed,
        int leftOut)
    {
        var channelText = channel == null ? "Channel unknown" : $"Channel {channel}";
        var end = first + Math.Max(visible, 1) - 1;
        var list = listed == 1 ? "1 listing on screen" : $"{listed} listings on screen";
        var miss = leftOut <= 0
            ? ""
            : leftOut == 1
                ? " · 1 left out"
                : $" · {leftOut} left out";
        return $"{owner} · {shop} · {map} · {channelText} · scrollbar {position.ToString("0.##", CultureInfo.InvariantCulture)} · rows {first}–{end} · {list}{miss}";
    }

    private void RenderOverlays()
    {
        if (ShopOverlay == null || TrainingOverlay == null || SampleBadge == null) return;
        var fm = _mode == ScannerMode.Fm;
        ShopOverlay.Visibility = fm && _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrainingOverlay.Visibility = !fm && _trainingReport.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var showSample = (fm && _sample && _rows.Count > 0) || (!fm && _trainingSample && _trainingReport.Length > 0);
        SampleBadge.Visibility = showSample ? Visibility.Visible : Visibility.Collapsed;
        if (!fm)
        {
            TrainingOverlayText.Text = _trainingReport;
            return;
        }

        ShopOverlayRows.Children.Clear();
        foreach (var row in _rows)
        {
            var border = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0xA1, 0x5A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                Tag = row,
            };
            border.MouseEnter += ShopRow_MouseEnter;
            border.MouseLeave += ShopRow_MouseLeave;
            border.Child = new TextBlock
            {
                Text = row.Sold
                    ? $"{row.Item} · sold · shop {row.PriceEach.ToString("N0", CultureInfo.InvariantCulture)}"
                    : $"{row.Item} · shop {row.PriceEach.ToString("N0", CultureInfo.InvariantCulture)}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x24, 0x1C)),
            };
            ShopOverlayRows.Children.Add(border);
        }
    }

    private void ShopRow_MouseEnter(object sender, MouseEventArgs e)
    {
        ShopRowView? view = sender switch
        {
            DataGridRow row => row.Item as ShopRowView,
            FrameworkElement element => element.Tag as ShopRowView,
            _ => null,
        };
        if (view == null || sender is not UIElement target) return;
        OverlayItem.Text = view.Item;
        OverlayShop.Text = $"Shop price {view.PriceEach.ToString("N0", CultureInfo.InvariantCulture)}";
        OverlayDb.Text = view.DbMeso == null
            ? (string.IsNullOrWhiteSpace(view.DbLabel) ? view.DbPrice : view.DbLabel)
            : $"{view.DbLabel} {view.DbPrice}";
        OverlayGap.Text = view.Gap;
        PricePopup.PlacementTarget = target;
        PricePopup.IsOpen = true;
    }

    private void ShopRow_MouseLeave(object sender, MouseEventArgs e) => PricePopup.IsOpen = false;

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_rows.Any(row => !row.DebugOnly))
        {
            StatusText.Text = "Scan a shop before exporting.";
            return;
        }

        var owner = _fm.Shop?.Owner ?? "Shop";
        var location = _fm.Shop == null
            ? "Free Market"
            : _fm.Shop.Channel == null
                ? _fm.Shop.MapName
                : $"{_fm.Shop.MapName} · Channel {_fm.Shop.Channel}";
        var store = new CompanionStore
        {
            Owner = owner,
            Location = location,
            ScannedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            Lines = _rows.Where(row => !row.DebugOnly).Select(row => new StoreLine
            {
                Item = row.Item,
                Quantity = row.Quantity,
                PriceEach = row.PriceEach,
                Slot = row.Slot,
            }).ToList(),
        };
        var json = ScanExporter.ToJson(new CompanionScanExport { Stores = [store] });
        var dialog = new SaveFileDialog
        {
            Filter = "Market Notes scan (*.json)|*.json",
            FileName = "market-notes-scan.json",
            Title = "Export shop scan",
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, json);
        StatusText.Text = _sample
            ? $"Wrote sample lines to {dialog.FileName}. They are not a live reading of the game."
            : $"Wrote {dialog.FileName}.";
    }

    private async void SendTrainingSample_Click(object sender, RoutedEventArgs e)
    {
        TrainingFixtureFile fixture;
        try
        {
            fixture = TrainingFixture.Load();
        }
        catch (Exception ex)
        {
            FarmStatus.Text = $"The training sample could not be opened ({ex.Message}).";
            return;
        }

        var (_, characterName) = CharacterIdentity();
        _training.ApplySample(fixture, characterName);
        _trainingSample = true;
        LootPoolSnapshot pool;
        string poolNote = "";
        try
        {
            _pool.ReplaceSession(_training);
            pool = _pool.Snapshot(characterName, fixture.MapId);
        }
        catch (Exception ex)
        {
            pool = LootPoolSnapshot.FromSession(_training);
            poolNote = $" The loot pool file could not be written ({ex.GetType().Name}). The rates below are this sitting.";
        }

        _trainingReport = TrainingReport.Format(_training, pool) + poolNote;
        FarmStatus.Text = "Sample training is on this screen.";
        FillDropGrid(_training);
        RenderOverlays();

        var (characterId, _) = CharacterIdentity();
        var mirror = await MirrorAsync(FeedUrlBox.Text, new
        {
            sessionId = $"training:{characterId}:{fixture.MapId}",
            mode = "replace",
            characterId,
            characterName,
            mapId = fixture.MapId,
            mapName = fixture.MapName,
            kills = 0,
            durationMs = 0,
            source = "fixture",
            ocr = false,
            battle = new { text = fixture.Battle.Text },
            frames = fixture.Frames.Select(frame => new
            {
                regionHash = frame.RegionHash,
                lines = frame.Lines,
                error = frame.Error,
            }),
        });
        if (!mirror.Attempted) return;
        FarmStatus.Text = mirror.Ok
            ? "A copy was sent to the website."
            : "The website is closed, so no copy was sent. The reading stays beside the drop list.";
    }

    private async Task<(bool Attempted, bool Ok, string Message)> MirrorAsync(string url, object body)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, false, "");
        var (ok, message) = await _feed.PostAsync(url, body, FeedToken());
        return (true, ok, message);
    }

    private void MirrorLater(string url, object body)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        // The token box belongs to the UI thread. Read it before leaving that thread.
        var token = FeedToken();
        Volatile.Write(ref _mirrorFailed, 0);
        _ = Task.Run(async () =>
        {
            try
            {
                var (ok, message) = await _feed.PostAsync(url, body, token);
                if (ok)
                {
                    Volatile.Write(ref _mirrorFailed, 0);
                    PublishMirrorNote(null);
                    return;
                }

                NoteMirrorFailure(message);
            }
            catch (Exception ex)
            {
                NoteMirrorFailure($"The website copy failed ({ex.GetType().Name}).");
            }
        });
    }

    private bool ShopMirrorDue()
    {
        return Volatile.Read(ref _mirrorFailed) == 1 && Environment.TickCount64 >= Volatile.Read(ref _nextMirrorAttempt);
    }

    private void NoteMirrorFailure(string message)
    {
        Volatile.Write(ref _mirrorFailed, 1);
        Volatile.Write(ref _nextMirrorAttempt, Environment.TickCount64 + 5000);
        PublishMirrorNote(message);
    }

    private void PublishMirrorNote(string? note)
    {
        void apply() => _mirrorNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (Dispatcher.CheckAccess()) apply();
        else Dispatcher.InvokeAsync(apply);
    }

    private void BugToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ApplyDebug(BugButton.IsChecked == true, save: true);
    }

    private void ApplyDebug(bool show, bool save)
    {
        DebugLog.Panel = show;
        DebugPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _overlay.SetDebug(show);
        if (_fm.Shop != null) ShowLocalShop(_sample);
        if (save) DebugUiStore.Save(show);
        if (show) TrainingSessionLog.Append(["debug logging on"]);
    }

    private async void UploadLogs_Click(object sender, RoutedEventArgs e)
    {
        UploadLogsButton.IsEnabled = false;
        try
        {
            var url = WebsiteAddress.LogsUrl(FeedUrlBox.Text);
            var (_, message) = await CompanionLogUpload.PostAsync(
                url,
                CompanionLogUpload.Tail(LootOcrLog.FilePath()),
                CompanionLogUpload.Tail(TrainingSessionLog.FilePath()),
                FeedToken());
            StatusText.Text = message;
        }
        finally
        {
            UploadLogsButton.IsEnabled = true;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadWebsiteSettings()
    {
        _loadingWebsite = true;
        var saved = WebsiteSettingsStore.Load();
        var url = saved.Found ? saved.Url.Trim() : "";
        var token = saved.Found ? saved.Token : "";
#if DEBUG
        if (url.Length == 0) url = WebsiteAddress.DevFeed;
#else
        if (url.Length == 0 || WebsiteAddress.IsLoopback(url)) url = WebsiteAddress.LiveFeed;
#endif
        FeedUrlBox.Text = url;
        FeedTokenBox.Text = token;
        _savedWebsiteUrl = url;
        _savedWebsiteToken = token;
        if (!saved.Found || !string.Equals(saved.Url.Trim(), url, StringComparison.Ordinal))
        {
            WebsiteSettingsStore.Save(url, token);
        }

        _loadingWebsite = false;
    }

    private void WebsiteSettings_Changed(object sender, TextChangedEventArgs e)
    {
        PersistWebsiteSettings();
    }

    private void PersistWebsiteSettings()
    {
        if (!_uiReady || _loadingWebsite) return;
        var url = FeedUrlBox.Text.Trim();
        var token = FeedTokenBox.Text.Trim();
        if (url == _savedWebsiteUrl && token == _savedWebsiteToken) return;
        WebsiteSettingsStore.Save(url, token);
        _savedWebsiteUrl = url;
        _savedWebsiteToken = token;
    }

    private string FeedToken()
    {
        return FeedTokenBox.Text.Trim();
    }

    private string FmUrl()
    {
        var farm = FeedUrlBox.Text.Trim();
        if (farm.Length == 0) return "";
        if (farm.Contains("/api/farm/feed", StringComparison.OrdinalIgnoreCase))
        {
            return farm.Replace("/api/farm/feed", "/api/fm/feed", StringComparison.OrdinalIgnoreCase);
        }

        return farm;
    }

    private void CharacterLive(CapturedFrame frame)
    {
        _identity.Read(frame);
        var watch = _sheet.Inspect(frame);
        if (watch.Stat != null)
        {
            // Rising edge: S just opened → always push the feed, even when OCR
            // numbers match the last successful post (reopen after equip/unequip).
            var opened = !_statWindowOpen;
            _statWindowOpen = true;
            if (opened)
            {
                _statSignature = null;
                _statMirrorPending = null;
                _statFeedSkipLogged = false;
                ShowStatOpenBanner("Character Stat window (S) detected, updating stats…");
                StatFeedLog.Write("open rising-edge force-clear signature; will push feed");
                TrainingSessionLog.Append(["character stat window opened; forcing website feed"]);
            }

            // Status-plate name is steadier than the OCR name on the Stat window
            // (that crop often drops a letter, e.g. MonkeDKof vs MonkeyDKof).
            var name = CharacterSheetName(watch.Stat.Name);
            var ocrName = watch.Stat.Name ?? "-";
            if (opened || name == null)
            {
                StatFeedLog.Write(
                    $"ocr name={ocrName} sheetName={name ?? "-"} lv={Fmt(watch.Stat.Level)} " +
                    $"str={Fmt(watch.Stat.Str)} dex={Fmt(watch.Stat.Dex)} int={Fmt(watch.Stat.Int)} luk={Fmt(watch.Stat.Luk)} " +
                    $"branch={watch.Stat.Branch ?? "-"} opened={opened}");
            }

            _statReport = StatWindowText.Describe(watch.Stat, false);
            if (opened) _statReport += " Stat opened — sending to the website.";
            if (name != null && watch.Stat.Name != null
                && !string.Equals(name, watch.Stat.Name, StringComparison.OrdinalIgnoreCase))
            {
                _statReport += $" Using status-plate name {name} (window said {watch.Stat.Name}).";
                if (opened) StatFeedLog.Write($"name-remap window={watch.Stat.Name} → sheet={name}");
            }

            _characterSample = false;
            if (name == null)
            {
                _statReport += " The name was not read, so no character was created.";
                if (!_statFeedSkipLogged)
                {
                    _statFeedSkipLogged = true;
                    StatFeedLog.Write("skip empty-parse: no character name from plate or Stat window");
                }
            }
            else
            {
                var signature = watch.Stat.Signature(name);
                // Only lock the signature after a successful POST. A failed
                // mirror (timeout / 401) used to stick forever and skip retries
                // while Stat stayed open on the same numbers. Opening S always
                // clears the lock above so reopen posts immediately.
                if (signature != _statSignature && signature != _statMirrorPending)
                {
                    _statMirrorPending = signature;
                    _statFeedSkipLogged = false;
                    StatFeedLog.Write(
                        $"ocr name={ocrName} sheetName={name} lv={Fmt(watch.Stat.Level)} " +
                        $"str={Fmt(watch.Stat.Str)} dex={Fmt(watch.Stat.Dex)} int={Fmt(watch.Stat.Int)} luk={Fmt(watch.Stat.Luk)} " +
                        $"branch={watch.Stat.Branch ?? "-"}");
                    StatFeedLog.Write($"push queued name={name} sig={ShortSig(signature)}");
                    MirrorCharacterLater(CharacterUrl(), watch.Stat, null, name, sample: false, mirrored =>
                    {
                        if (mirrored) _statSignature = signature;
                        if (_statMirrorPending == signature) _statMirrorPending = null;
                    });
                }
                else if (opened)
                {
                    StatFeedLog.Write($"skip unexpected: rising-edge but signature still locked sig={ShortSig(signature)}");
                }
                else if (!_statFeedSkipLogged)
                {
                    _statFeedSkipLogged = true;
                    if (signature == _statMirrorPending)
                        StatFeedLog.Write($"skip post-in-flight sig={ShortSig(signature)}");
                    else
                        StatFeedLog.Write($"skip signature-unchanged sig={ShortSig(signature)} (Stat still open after successful push)");
                }
            }
        }
        else
        {
            if (_statWindowOpen)
            {
                StatFeedLog.Write("close Stat window no longer on frame");
                TrainingSessionLog.Append(["character stat window closed"]);
                HideStatOpenBanner();
            }

            _statWindowOpen = false;
            _statFeedSkipLogged = false;
            // Stat closed: drop the mirror lock so the next open is a fresh edge.
            _statSignature = null;
            _statMirrorPending = null;

            if (_identity.StatusBar is { Succeeded: true } plate)
            {
                // Still push LV + max HP from the bottom plate.
                var name = CharacterSheetName(plate.Name);
                _statReport = StatusBarText.Describe(plate);
                if (name == null)
                {
                    _statReport += " The name was not read, so no character was updated.";
                }
                else
                {
                    _statReport += " Open Stat (S) to sync STR/DEX and the rest.";
                    var signature = "status|" + name + "|" + plate.Signature();
                    if (signature != _statusSignature && signature != _statusMirrorPending)
                    {
                        _statusMirrorPending = signature;
                        MirrorStatusBarLater(CharacterUrl(), plate, name, mirrored =>
                        {
                            if (mirrored) _statusSignature = signature;
                            if (_statusMirrorPending == signature) _statusMirrorPending = null;
                        });
                    }
                }

                _characterSample = false;
            }
        }

        if (watch.Skill != null)
        {
            var name = CharacterSheetName(null);
            _skillReport = SkillWindowText.Describe(watch.Skill, name, false);
            _characterSample = false;
            if (name == null)
            {
                _skillReport += " The name was not read, so no character was created.";
            }
            else
            {
                var signature = watch.Skill.Signature(name);
                if (signature != _skillSignature && signature != _skillMirrorPending)
                {
                    _skillMirrorPending = signature;
                    MirrorCharacterLater(CharacterUrl(), null, watch.Skill, name, sample: false, mirrored =>
                    {
                        if (mirrored) _skillSignature = signature;
                        if (_skillMirrorPending == signature) _skillMirrorPending = null;
                    });
                }
            }
        }

        ShowCharacterStatus(watch.Status);
    }

    private void MirrorStatusBarLater(string url, StatusBarSheet plate, string name, Action<bool>? done = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _statReport += " Website URL is not a farm feed (/api/farm/feed), so the plate was not sent.";
            ShowCharacterStatus(null);
            done?.Invoke(false);
            return;
        }

        var token = FeedToken();
        var profileUrl = ProfileUrl();
        _ = Task.Run(async () =>
        {
            string note;
            var mirrored = false;
            try
            {
                var matched = await MatchLedgerNameAsync(name, token, profileUrl) ?? name;
                var body = new
                {
                    kind = "stat",
                    name = matched,
                    branch = plate.Branch,
                    level = plate.Level,
                    str = (int?)null,
                    dex = (int?)null,
                    @int = (int?)null,
                    luk = (int?)null,
                    maxHp = plate.MaxHp,
                    maxMp = plate.MaxMp,
                    weaponAttack = (int?)null,
                    magicAttack = (int?)null,
                    accuracy = (int?)null,
                    weaponDef = (int?)null,
                    magicDef = (int?)null,
                    sample = false,
                    ocr = true,
                };
                var (ok, message) = await _feed.PostAsync(url, body, token);
                var mapped = !string.Equals(matched, name, StringComparison.OrdinalIgnoreCase)
                    ? $" Matched ledger name {matched}."
                    : "";
                note = ok ? mapped + " Sent to the website." : " Website copy failed: " + message;
                mirrored = ok;
                if (!ok) NoteMirrorFailure(message);
                else
                {
                    Volatile.Write(ref _mirrorFailed, 0);
                    PublishMirrorNote(null);
                }
            }
            catch (Exception ex)
            {
                note = $" Website copy failed ({ex.GetType().Name}: {ex.Message}).";
                NoteMirrorFailure(note.Trim());
            }

            TrainingSessionLog.Append([$"character plate mirror {(mirrored ? "ok" : "fail")}: {note.Trim()}"]);
            await Dispatcher.InvokeAsync(() =>
            {
                _statReport += note;
                ShowCharacterStatus(null);
                done?.Invoke(mirrored);
            });
        });
    }

    /// <summary>
    /// Posts a character sheet and writes the website reply onto the Character
    /// status line so a bad URL/token is visible there, not only in Seen.
    /// OCR names are remapped onto the account ledger first (MonkeDKof → MonkeyDKof).
    /// </summary>
    private void MirrorCharacterLater(
        string url,
        StatSheet? stat,
        SkillSheet? skill,
        string? ocrName,
        bool sample,
        Action<bool>? done = null)
    {
        var kind = stat != null ? "stat" : "skill";
        if (string.IsNullOrWhiteSpace(url))
        {
            var missing = " Website URL is not a farm feed (/api/farm/feed), so the sheet was not sent.";
            if (stat != null) _statReport += missing;
            else _skillReport += missing;
            if (stat != null)
                StatFeedLog.Write("skip bad-url: farm feed box empty or not /api/farm/feed (no /api/character/feed)");
            ShowCharacterStatus(null);
            done?.Invoke(false);
            return;
        }

        var token = FeedToken();
        var profileUrl = ProfileUrl();
        if (stat != null)
            StatFeedLog.Write($"post-start kind={kind} url={url} hasToken={!string.IsNullOrWhiteSpace(token)} ocrName={ocrName ?? "-"}");
        _ = Task.Run(async () =>
        {
            string note;
            var mirrored = false;
            try
            {
                var name = await MatchLedgerNameAsync(ocrName, token, profileUrl);
                if (stat != null && name != null && ocrName != null
                    && !string.Equals(name, ocrName, StringComparison.OrdinalIgnoreCase))
                {
                    StatFeedLog.Write($"ledger-match {ocrName} → {name}");
                }

                object body = stat != null
                    ? StatPayload(stat, name, sample)
                    : SkillPayload(skill!, name, sample);
                var posted = await _feed.PostForBodyAsync(url, body, token);
                var mapped = name != null && ocrName != null
                    && !string.Equals(name, ocrName, StringComparison.OrdinalIgnoreCase)
                    ? $" Matched ledger name {name}."
                    : "";
                note = posted.Ok ? mapped + " Sent to the website." : " Website copy failed: " + posted.Message;
                mirrored = posted.Ok;
                if (stat != null)
                {
                    var bodySnippet = posted.Body.Length > 160 ? posted.Body[..160] : posted.Body;
                    bodySnippet = bodySnippet.Replace('\r', ' ').Replace('\n', ' ');
                    StatFeedLog.Write(
                        posted.Ok
                            ? $"post-ok kind={kind} name={name ?? "-"} url={url}"
                            : $"post-fail kind={kind} name={name ?? "-"} url={url} message={posted.Message} body={bodySnippet}");
                }

                if (!posted.Ok) NoteMirrorFailure(posted.Message);
                else
                {
                    Volatile.Write(ref _mirrorFailed, 0);
                    PublishMirrorNote(null);
                }
            }
            catch (Exception ex)
            {
                note = $" Website copy failed ({ex.GetType().Name}: {ex.Message}).";
                if (stat != null)
                    StatFeedLog.Write($"post-error kind={kind} url={url} ex={ex.GetType().Name}: {ex.Message}");
                NoteMirrorFailure(note.Trim());
            }

            TrainingSessionLog.Append([
                $"character {kind} mirror {(mirrored ? "ok" : "fail")}: {note.Trim()}"]);
            await Dispatcher.InvokeAsync(() =>
            {
                if (stat != null) _statReport += note;
                else _skillReport += note;
                ShowCharacterStatus(null);
                done?.Invoke(mirrored);
            });
        });
    }

    private static string Fmt(int? value) => value?.ToString() ?? "-";

    private static string ShortSig(string signature)
    {
        if (signature.Length <= 48) return signature;
        return signature[..48] + "…";
    }

    private async Task<string?> MatchLedgerNameAsync(string? ocrName, string? token, string profileUrl)
    {
        if (string.IsNullOrWhiteSpace(ocrName)) return ocrName;
        var names = await LoadLedgerNamesAsync(token, profileUrl);
        return LedgerNameMatch.Prefer(ocrName, names);
    }

    private async Task<IReadOnlyList<string>> LoadLedgerNamesAsync(string? token, string profileUrl)
    {
        var now = Environment.TickCount64;
        if (_ledgerNames.Count > 0 && now - _ledgerNamesAt < 60_000) return _ledgerNames;
        // profileUrl must be captured on the UI thread — FeedUrlBox is not
        // safe to read from the feed Task.Run (WPF InvalidOperationException).
        if (profileUrl.Length == 0 || string.IsNullOrWhiteSpace(token)) return _ledgerNames;

        var (ok, _, body) = await _feed.GetAsync(profileUrl, token);
        if (!ok) return _ledgerNames;
        var names = LedgerNameMatch.NamesFromProfileJson(body);
        if (names.Count == 0) return _ledgerNames;
        _ledgerNames = names;
        _ledgerNamesAt = now;
        return _ledgerNames;
    }

    private string ProfileUrl()
    {
        var farm = FeedUrlBox.Text.Trim();
        if (farm.Length == 0) return "";
        if (farm.Contains("/api/farm/feed", StringComparison.OrdinalIgnoreCase))
        {
            return farm.Replace("/api/farm/feed", "/api/profile", StringComparison.OrdinalIgnoreCase);
        }

        return "";
    }

    private async void LoadStatSample_Click(object sender, RoutedEventArgs e)
    {
        StatWindowFixture fixture;
        try
        {
            fixture = CharacterSheetFixture.LoadStat();
        }
        catch (Exception ex)
        {
            CharacterStatus.Text = $"The stat sample could not be opened ({ex.Message}).";
            return;
        }

        var sheet = StatWindowText.Parse(fixture.Text);
        _statReport = StatWindowText.Describe(sheet, true);
        _characterSample = true;
        ShowCharacterStatus(null);
        if (!sheet.Succeeded) return;
        var name = sheet.Name;
        _statSignature = sheet.Signature(name);
        var mirror = await MirrorAsync(CharacterUrl(), StatPayload(sheet, name, sample: true));
        if (!mirror.Attempted) return;
        _statReport += mirror.Ok
            ? " A copy was sent to the website."
            : " The website is closed, so no copy was sent. This sheet stays on the screen.";
        ShowCharacterStatus(null);
    }

    private async void LoadSkillSample_Click(object sender, RoutedEventArgs e)
    {
        SkillWindowFixture fixture;
        try
        {
            fixture = CharacterSheetFixture.LoadSkill();
        }
        catch (Exception ex)
        {
            CharacterStatus.Text = $"The skill sample could not be opened ({ex.Message}).";
            return;
        }

        var sheet = SkillWindowText.Parse(fixture.Text);
        var name = ScreenLabel.AcceptCharacter(fixture.CharacterName);
        _skillReport = SkillWindowText.Describe(sheet, name, true);
        if (name == null) _skillReport += " The name was not read, so no character was created.";
        _characterSample = true;
        ShowCharacterStatus(null);
        if (!sheet.Succeeded) return;
        _skillSignature = sheet.Signature(name);
        var mirror = await MirrorAsync(CharacterUrl(), SkillPayload(sheet, name, sample: true));
        if (!mirror.Attempted) return;
        _skillReport += mirror.Ok
            ? " A copy was sent to the website."
            : " The website is closed, so no copy was sent. This sheet stays on the screen.";
        ShowCharacterStatus(null);
    }

    private void ShowCharacterStatus(string? live)
    {
        if (CharacterStatus == null) return;
        var text = "";
        if (_statReport.Length > 0) text += _statReport + "\n";
        if (_skillReport.Length > 0) text += _skillReport + "\n";
        if (!string.IsNullOrWhiteSpace(live)) text += live;
        if (_characterSample && !string.IsNullOrWhiteSpace(live))
        {
            text += "\nThe numbers above are still the sample.";
        }

        CharacterStatus.Text = text.Trim();
    }

    private void ShowStatOpenBanner(string message)
    {
        if (StatOpenBanner == null || StatOpenBannerText == null) return;
        // Banner lives under the training DropGrid above Farm/Character status.
        if (TrainingPanel != null) TrainingPanel.Visibility = Visibility.Visible;
        if (FmPanel != null) FmPanel.Visibility = Visibility.Collapsed;
        StatOpenBannerText.Text = message;
        StatOpenBanner.Visibility = Visibility.Visible;
    }

    private void HideStatOpenBanner()
    {
        if (StatOpenBanner == null) return;
        StatOpenBanner.Visibility = Visibility.Collapsed;
    }

    private static object StatPayload(StatSheet sheet, string? name, bool sample)
    {
        return new
        {
            kind = "stat",
            name,
            branch = sheet.Branch,
            level = sheet.Level,
            str = sheet.Str,
            dex = sheet.Dex,
            @int = sheet.Int,
            luk = sheet.Luk,
            maxHp = sheet.MaxHp,
            maxMp = sheet.MaxMp,
            weaponAttack = sheet.WeaponAttack,
            magicAttack = sheet.MagicAttack,
            accuracy = sheet.Accuracy,
            weaponDef = sheet.WeaponDef,
            magicDef = sheet.MagicDef,
            sample,
            ocr = !sample && WindowsOcr.IsAvailable,
        };
    }

    private static object SkillPayload(SkillSheet sheet, string? name, bool sample)
    {
        return new
        {
            kind = "skill",
            name,
            skills = sheet.Skills.Select(skill => new { id = skill.Id, level = skill.Level }).ToArray(),
            skipped = sheet.Skipped,
            sample,
            ocr = !sample && WindowsOcr.IsAvailable,
        };
    }

    /// <summary>
    /// Prefer the held status-plate name; fall back to the window OCR name.
    /// When both are close OCR misspellings, keep the longer one.
    /// </summary>
    private string? CharacterSheetName(string? windowName)
    {
        var plate = ScreenLabel.AcceptCharacter(_identity.CharacterName);
        var window = ScreenLabel.AcceptCharacter(windowName);
        if (plate != null && window != null && ScreenLabel.NamesClose(plate, window))
        {
            return plate.Length >= window.Length ? plate : window;
        }

        // Stat window wins when the held plate name is a different character
        // (switching chars left the hold on the previous name).
        if (window != null && plate != null && !ScreenLabel.NamesClose(plate, window))
        {
            return window;
        }

        if (plate != null) return plate;
        return window;
    }

    private string CharacterUrl()
    {
        var farm = FeedUrlBox.Text.Trim();
        if (farm.Length == 0) return "";
        if (farm.Contains("/api/farm/feed", StringComparison.OrdinalIgnoreCase))
        {
            return farm.Replace("/api/farm/feed", "/api/character/feed", StringComparison.OrdinalIgnoreCase);
        }

        return "";
    }

    private (string Id, string Name) CharacterIdentity()
    {
        var name = string.IsNullOrWhiteSpace(CharacterNameBox.Text) ? "Sample hunter" : CharacterNameBox.Text.Trim();
        if (name.Length > 32) name = name[..32];
        var typed = CharacterIdBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(typed)) return (typed.Length > 80 ? typed[..80] : typed, name);
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "hunter";
        return ($"name:{slug}", name);
    }
}
