using System.Diagnostics;
using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Farm;

public sealed class DropWatchResult
{
    public required string Status { get; init; }
    public required bool RegionChanged { get; init; }
    public required bool CaptureOk { get; init; }
    public string? RegionHash { get; init; }
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
    public IReadOnlyList<string> Words { get; init; } = [];
    /// <summary>Top of each line in crop pixels. Null when the reader gives no positions (Windows OCR).</summary>
    public IReadOnlyList<int>? Tops { get; init; }
    /// <summary>When the crop was grabbed, Unix ms.</summary>
    public long AtMs { get; init; }
    /// <summary>The image the reader saw: the raw crop for PaddleOCR, LootInk.IsolateNotice for Windows OCR. Null when nothing was read.</summary>
    public byte[]? Isolated { get; init; }
    public int IsolatedWidth { get; init; }
    public int IsolatedHeight { get; init; }
}

/// <summary>Reads finished since the last drain, oldest first, and how the loop is doing.</summary>
public sealed class LootDrain
{
    public required IReadOnlyList<DropWatchResult> Reads { get; init; }
    public required bool CaptureOk { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// The loot corner has its own loop, apart from the 500 ms share tick:
/// inventory-full spam turns the six-line stack over in about 600 ms, so
/// lines came and went between ticks. A capture thread grabs the window at
/// up to <see cref="MaxFps"/> and keeps only the newest changed crop.
/// <see cref="Readers"/> background threads (a quarter of the logical
/// processors, at most eight) share one PaddleOCR and each take the newest
/// waiting crop. The share tick drains finished reads in capture order;
/// a read is held back while an older one is still running.
/// </summary>
public sealed class DropNoticeWatch
{
    public const int MaxFps = 60;
    public static readonly int Readers = Math.Clamp(Environment.ProcessorCount / 4, 1, 8);

    private readonly object _gate = new();
    private readonly SortedDictionary<long, DropWatchResult> _done = new();
    private readonly SortedSet<long> _inFlight = new();
    private (RegionBitmap Crop, string Hash, long Seq, long AtMs)? _waiting;
    private CancellationTokenSource? _stop;
    private ShareSource? _source;
    private long _seq;
    private int _generation;
    private bool _captureOk = true;
    private string _captureNote = "";
    private PixelRect? _cover;
    private int _grabs;
    private int _reads;

    /// <summary>
    /// The battle analysis window, in frame pixels, when the share tick last saw it.
    /// Dragged over the loot corner, its rows sat beside the notices, joined into
    /// their lines, and moved them from read to read, so pickups counted again.
    /// That part of the crop is blacked out.
    /// </summary>
    public PixelRect? Cover
    {
        get { lock (_gate) return _cover; }
        set { lock (_gate) _cover = value; }
    }
    private long _statsFrom = Environment.TickCount64;
    private string _rate = "";

    /// <summary>Starts the loop for this share, or keeps the one already running for it.</summary>
    public void Run(IScreenCapture capture, ShareSource source)
    {
        lock (_gate)
        {
            if (_stop != null && ReferenceEquals(_source, source)) return;
        }

        Reset();
        var stop = new CancellationTokenSource();
        int generation;
        lock (_gate)
        {
            _stop = stop;
            _source = source;
            generation = _generation;
        }

        var grabber = new Thread(() => Capture(capture, source, generation, stop.Token))
        {
            IsBackground = true,
            Name = "loot capture",
            Priority = ThreadPriority.BelowNormal,
        };
        grabber.Start();
        for (var i = 0; i < Readers; i++)
        {
            var reader = new Thread(() => Read(generation, stop.Token))
            {
                IsBackground = true,
                Name = "loot reader " + i,
                Priority = ThreadPriority.BelowNormal,
            };
            reader.Start();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            _stop = null;
            _source = null;
            _generation++;
            _waiting = null;
            _done.Clear();
            _inFlight.Clear();
            _captureOk = true;
            _captureNote = "";
            Monitor.PulseAll(_gate);
        }
    }

    public LootDrain Drain()
    {
        lock (_gate)
        {
            var oldestRunning = _inFlight.Count > 0 ? _inFlight.Min : long.MaxValue;
            var ready = _done.Where(pair => pair.Key < oldestRunning).ToList();
            foreach (var pair in ready) _done.Remove(pair.Key);
            var now = Environment.TickCount64;
            if (now - _statsFrom >= 5000)
            {
                var seconds = (now - _statsFrom) / 1000.0;
                _rate = $"Loot corner: {_grabs / seconds:0} grabs/s, {_reads / seconds:0.0} reads/s on {Readers} readers.";
                _grabs = 0;
                _reads = 0;
                _statsFrom = now;
            }

            var status = _stop == null ? "The loot loop is not running."
                : !_captureOk ? "The loot grab failed. " + _captureNote
                : _rate.Length > 0 ? _rate
                : $"Loot corner: reading on {Readers} readers.";
            return new LootDrain
            {
                Reads = ready.Select(pair => pair.Value).ToList(),
                CaptureOk = _captureOk,
                Status = status,
            };
        }
    }

    private static void Blank(RegionBitmap crop, PixelRect loot, PixelRect cover)
    {
        const int pad = 4;
        var x0 = Math.Max(0, cover.X - pad - loot.X);
        var y0 = Math.Max(0, cover.Y - pad - loot.Y);
        var x1 = Math.Min(crop.Width, cover.Right + pad - loot.X);
        var y1 = Math.Min(crop.Height, cover.Bottom + pad - loot.Y);
        for (var y = y0; y < y1; y++)
        {
            var row = y * crop.Width * 4;
            for (var x = x0; x < x1; x++)
            {
                var at = row + x * 4;
                crop.Bgra[at] = 0;
                crop.Bgra[at + 1] = 0;
                crop.Bgra[at + 2] = 0;
                crop.Bgra[at + 3] = 255;
            }
        }
    }

    private void Capture(IScreenCapture capture, ShareSource source, int generation, CancellationToken stop)
    {
        var period = TimeSpan.FromSeconds(1.0 / MaxFps);
        var clock = Stopwatch.StartNew();
        string? lastHash = null;
        while (!stop.IsCancellationRequested)
        {
            var started = clock.Elapsed;
            var ok = false;
            var note = "";
            try
            {
                var frame = capture.Grab(source);
                if (frame == null)
                {
                    note = "The grab returned no pixels.";
                }
                else
                {
                    var box = DropRegion.Box(frame);
                    var crop = RegionCrop.CropRect(frame, box.X, box.Y, box.Width, box.Height);
                    if (crop.Ok && Cover is { } cover) Blank(crop, box, cover);
                    if (!crop.Ok)
                    {
                        note = "The drop-notice crop was empty.";
                    }
                    else
                    {
                        ok = true;
                        var hash = DropRegion.Hash(crop.Bgra);
                        if (hash != lastHash)
                        {
                            lastHash = hash;
                            lock (_gate)
                            {
                                if (generation != _generation) return;
                                _waiting = (crop, hash, ++_seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                                Monitor.Pulse(_gate);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                note = $"The grab failed ({ex.GetType().Name}).";
            }

            lock (_gate)
            {
                if (generation != _generation) return;
                _captureOk = ok;
                _captureNote = note;
                _grabs++;
            }

            var rest = period - (clock.Elapsed - started);
            if (rest > TimeSpan.Zero) Thread.Sleep(rest);
        }
    }

    private void Read(int generation, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            (RegionBitmap Crop, string Hash, long Seq, long AtMs) job;
            lock (_gate)
            {
                while (_waiting == null && !stop.IsCancellationRequested && generation == _generation)
                {
                    Monitor.Wait(_gate, 250);
                }

                if (stop.IsCancellationRequested || generation != _generation || _waiting == null) return;
                job = _waiting.Value;
                _waiting = null;
                _inFlight.Add(job.Seq);
            }

            DropWatchResult result;
            try
            {
                result = ReadCrop(job.Crop, job.Hash, job.AtMs);
            }
            catch (Exception ex)
            {
                result = new DropWatchResult
                {
                    Status = $"The loot read failed ({ex.GetType().Name}).",
                    RegionChanged = false,
                    CaptureOk = true,
                    AtMs = job.AtMs,
                };
            }

            lock (_gate)
            {
                _inFlight.Remove(job.Seq);
                if (generation != _generation) return;
                _done[job.Seq] = result;
                _reads++;
            }
        }
    }

    private static DropWatchResult ReadCrop(RegionBitmap crop, string hash, long atMs)
    {
        byte[] seen;
        List<string> lines;
        List<string> words;
        List<int>? tops;
        string engine;
        if (PaddleOcr.IsAvailable)
        {
            seen = crop.Bgra;
            var placed = PaddleOcr.Read(crop.Bgra, crop.Width, crop.Height);
            lines = placed.Select(line => line.Text).ToList();
            tops = placed.Select(line => line.Top).ToList();
            words = lines.Select(_ => "").ToList();
            engine = "PaddleOCR";
        }
        else
        {
            seen = LootInk.IsolateNotice(crop.Bgra, crop.Width, crop.Height);
            var detail = OcrDropNoticeReader.EngineInstalled()
                ? WindowsOcr.ReadDetail(seen, crop.Width, crop.Height)
                : [];
            lines = detail.Select(line => line.Text).ToList();
            words = detail.Select(line => line.Words).ToList();
            tops = null;
            engine = "Windows OCR";
        }

        var status = PaddleOcr.IsAvailable || OcrDropNoticeReader.EngineInstalled()
            ? lines.Count == 0
                ? $"Drop notice changed ({hash}). {engine} read no text in the loot corner."
                : $"Drop notice changed ({hash}). " + string.Join(" | ", lines)
            : $"Drop notice changed ({hash}), {crop.Bgra.Length} bytes in the bottom-right of the window. {PaddleOcr.Status} {WindowsOcr.Status}";
        return new DropWatchResult
        {
            Status = status,
            RegionChanged = true,
            CaptureOk = true,
            RegionHash = hash,
            CropWidth = crop.Width,
            CropHeight = crop.Height,
            Lines = lines,
            Words = words,
            Tops = tops,
            AtMs = atMs,
            Isolated = seen,
            IsolatedWidth = crop.Width,
            IsolatedHeight = crop.Height,
        };
    }
}
