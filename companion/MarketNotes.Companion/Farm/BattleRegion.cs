using System.Text.RegularExpressions;
using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Farm;

public sealed class BattleWatchResult
{
    public required string Status { get; init; }
    public required bool RegionChanged { get; init; }
    public required bool CaptureOk { get; init; }
    public string? RegionHash { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
    /// <summary>True when each stat was read from its own row instead of the whole panel.</summary>
    public bool FromColumns { get; init; }
    public PixelRect? Window { get; init; }
    public BattleButton Button { get; init; }
    public bool Held { get; init; }
    public bool Lost { get; init; }
    /// <summary>BattleInk.Isolate output for this changed crop. Null when the crop was skipped.</summary>
    public byte[]? Isolated { get; init; }
    public int IsolatedWidth { get; init; }
    public int IsolatedHeight { get; init; }
    /// <summary>Value cells OCR was given when the panel was sliced. Empty when it was not.</summary>
    public IReadOnlyList<BattleColumnCrop> ValueCells { get; init; } = [];
}

/// <summary>
/// Finds the battle analysis window from its START or CANCEL button and
/// reads that box. A missing button keeps the last good rectangle for a
/// short time. It is never a reason to read the middle of the map.
/// </summary>
public sealed class BattleRegionWatch
{
    private readonly BattleWindowFinder.Track _track = new();
    private string? _lastHash;

    /// <summary>
    /// PaddleOCR reads the panel on its own thread (about 180 ms a read, too slow
    /// for the share tick). The tick hands over each changed crop and takes back
    /// the newest finished read, so a figure shows one tick later than it changed.
    /// </summary>
    private readonly object _gate = new();
    private Thread? _reader;
    private int _generation;
    private (RegionBitmap Crop, string Hash, int Generation)? _pending;
    private PanelRead? _ready;

    private sealed record PanelRead(string Hash, IReadOnlyList<string> Lines, byte[] Isolated, int Width, int Height);

    public void Reset()
    {
        _lastHash = null;
        _track.Reset();
        Drop();
    }

    private void Drop()
    {
        lock (_gate)
        {
            _generation++;
            _pending = null;
            _ready = null;
        }
    }

    public BattleWatchResult Inspect(CapturedFrame? frame)
    {
        if (frame == null)
        {
            return new BattleWatchResult
            {
                Status = "The grab returned no pixels.",
                RegionChanged = false,
                CaptureOk = false,
            };
        }

        if (frame.Bgra.Length == 0 || frame.Height <= 0 || frame.Bgra.Length % frame.Height != 0)
        {
            return new BattleWatchResult
            {
                Status = "The battle analysis crop was empty.",
                RegionChanged = false,
                CaptureOk = false,
            };
        }

        var stride = frame.Bgra.Length / frame.Height;
        var sight = _track.ObserveSight(frame.Bgra, frame.Width, frame.Height, stride);
        if (sight.Window == null)
        {
            _lastHash = null;
            Drop();
            return new BattleWatchResult
            {
                Status = sight.Lost
                    ? "The battle analysis window is gone."
                    : "The battle analysis window is not on this frame.",
                RegionChanged = false,
                CaptureOk = true,
                Button = sight.Button,
                Held = sight.Held,
                Lost = sight.Lost,
            };
        }

        var window = sight.Window.Value;
        var crop = RegionCrop.CropRect(frame, window.X, window.Y, window.Width, window.Height);
        if (!crop.Ok)
        {
            return new BattleWatchResult
            {
                Status = "The battle analysis crop was empty.",
                RegionChanged = false,
                CaptureOk = false,
                Window = window,
                Button = sight.Button,
                Held = sight.Held,
                Lost = false,
            };
        }

        var hash = DropRegion.Hash(crop.Bgra);
        if (PaddleOcr.IsAvailable) return Handoff(crop, hash, window, sight);
        if (hash == _lastHash)
        {
            return new BattleWatchResult
            {
                Status = $"Battle analysis unchanged ({hash}). Skipped reading.",
                RegionChanged = false,
                CaptureOk = true,
                RegionHash = hash,
                Window = window,
                Button = sight.Button,
                Held = sight.Held,
                Lost = false,
            };
        }

        _lastHash = hash;
        var fromColumns = false;
        IReadOnlyList<string> lines = [];
        IReadOnlyList<BattleColumnCrop> cells = [];
        byte[]? panel = null;
        if (OcrRegionReader.EngineInstalled())
        {
            var columns = BattleColumns.Slice(crop.Bgra, crop.Width, crop.Height);
            cells = columns;
            if (columns.Count >= 4)
            {
                var named = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var column in columns)
                {
                    var valueText = ReadCrop(column.Pixels, column.Width, column.Height);
                    var labelText = column.LabelWidth > 1
                        ? ReadCrop(column.LabelPixels, column.LabelWidth, column.LabelHeight)
                        : "";
                    var name = BattleColumns.NameFromLabel(labelText);
                    if (name == null) continue;
                    if (name == "Mob" && Millions(valueText)) continue;
                    if (!named.ContainsKey(name) || named[name].Length == 0) named[name] = valueText;
                }

                var built = new List<string>();
                foreach (var name in new[] { "Dmg", "Mob", "Exp", "Meso", "Elapsed" })
                {
                    if (!named.TryGetValue(name, out var value)) continue;
                    built.Add(name + ": " + value);
                }

                // A single scraped rate ("Meso: (566/m)") used to win the column
                // path and hide a panel read that still had every figure.
                if (named.TryGetValue("Dmg", out var dmg) && dmg.Any(char.IsDigit)
                    && named.TryGetValue("Mob", out var mob) && mob.Any(char.IsDigit)
                    && named.TryGetValue("Exp", out var exp) && exp.Any(char.IsDigit))
                {
                    lines = built;
                    fromColumns = true;
                }
            }

            if (!fromColumns)
            {
                panel = BattleInk.Isolate(crop.Bgra, crop.Width, crop.Height);
                lines = OcrRegionReader.ReadLines(panel, crop.Width, crop.Height);
            }
        }

        // Column OCR reads padded cells cut from this same mask. The full panel
        // is what the fallback reads, and what the preview shows either way.
        panel ??= BattleInk.Isolate(crop.Bgra, crop.Width, crop.Height);
        InkSampleDump.Save("battle", crop.Bgra, crop.Width, crop.Height, string.Join(" | ", lines));

        var status = OcrRegionReader.EngineInstalled()
            ? lines.Count == 0
                ? $"Battle analysis changed ({hash}). Windows OCR read no text in that box."
                : $"Battle analysis changed ({hash}). " + (fromColumns ? "columns " : "") + string.Join(" | ", lines)
            : $"Battle analysis changed ({hash}), {crop.Bgra.Length} bytes. {Scanning.WindowsOcr.Status}";
        return Accept(new BattleWatchResult
        {
            Status = status,
            RegionChanged = true,
            CaptureOk = true,
            RegionHash = hash,
            Lines = lines,
            FromColumns = fromColumns,
            Window = window,
            Button = sight.Button,
            Held = sight.Held,
            Lost = false,
            Isolated = panel,
            IsolatedWidth = crop.Width,
            IsolatedHeight = crop.Height,
            ValueCells = cells,
        });
    }

    private BattleWatchResult Handoff(RegionBitmap crop, string hash, PixelRect window, BattleSight sight)
    {
        PanelRead? done;
        bool reading;
        lock (_gate)
        {
            reading = _pending != null || hash != _lastHash;
            if (hash != _lastHash)
            {
                _lastHash = hash;
                _pending = (crop, hash, _generation);
                Monitor.Pulse(_gate);
            }

            done = _ready;
            _ready = null;
            if (_reader == null)
            {
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "battle reader", Priority = ThreadPriority.BelowNormal };
                _reader.Start();
            }
        }

        if (done == null)
        {
            return new BattleWatchResult
            {
                Status = reading ? $"Battle analysis ({hash}) is being read." : $"Battle analysis unchanged ({hash}). Skipped reading.",
                RegionChanged = false,
                CaptureOk = true,
                RegionHash = hash,
                Window = window,
                Button = sight.Button,
                Held = sight.Held,
                Lost = false,
            };
        }

        return Accept(new BattleWatchResult
        {
            Status = done.Lines.Count == 0
                ? $"Battle analysis changed ({done.Hash}). PaddleOCR read no text in that box."
                : $"Battle analysis changed ({done.Hash}). " + string.Join(" | ", done.Lines),
            RegionChanged = true,
            CaptureOk = true,
            RegionHash = done.Hash,
            Lines = done.Lines,
            Window = window,
            Button = sight.Button,
            Held = sight.Held,
            Lost = false,
            Isolated = done.Isolated,
            IsolatedWidth = done.Width,
            IsolatedHeight = done.Height,
        });
    }

    /// <summary>
    /// A crop that reads as chat or a loot line is not the panel. Drop it so
    /// the guide does not stay on that spot.
    /// </summary>
    private BattleWatchResult Accept(BattleWatchResult result)
    {
        if (result.Lines.Count == 0 || result.Window is not { } window) return result;
        if (BattleWindowFinder.LooksLikePanel(result.Lines)) return result;
        _track.Dismiss(window);
        return new BattleWatchResult
        {
            Status = "That box is not the battle analysis window.",
            RegionChanged = false,
            CaptureOk = result.CaptureOk,
        };
    }

    private void ReadLoop()
    {
        while (true)
        {
            (RegionBitmap Crop, string Hash, int Generation) job;
            lock (_gate)
            {
                while (_pending == null) Monitor.Wait(_gate);
                job = _pending.Value;
                _pending = null;
            }

            var crop = job.Crop;
            var lines = PaddleOcr.Read(crop.Bgra, crop.Width, crop.Height, splitGaps: false).Select(line => line.Text).ToList();
            var panel = BattleInk.Isolate(crop.Bgra, crop.Width, crop.Height);
            InkSampleDump.Save("battle", crop.Bgra, crop.Width, crop.Height, string.Join(" | ", lines));
            lock (_gate)
            {
                if (job.Generation == _generation) _ready = new PanelRead(job.Hash, lines, panel, crop.Width, crop.Height);
            }
        }
    }

    private static bool Millions(string text) => Regex.IsMatch(text, @"\d\.\d+\s*M\b", RegexOptions.IgnoreCase);

    private static string ReadCrop(byte[] pixels, int width, int height)
    {
        if (pixels.Length == 0 || width <= 0 || height <= 0) return "";
        var read = OcrRegionReader.ReadLines(pixels, width, height);
        return string.Join(" ", read.Select(line => line.Trim()).Where(line => line.Length > 0));
    }
}
