using System.Text.RegularExpressions;
using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// Reads the minimap title band from <see cref="UiChromeFinder"/> and the
/// fixed name plate. A bad frame keeps the last confident label.
/// </summary>
public sealed class TrainingIdentityReader
{
    private readonly LabelHold _character = new(ScreenLabel.AcceptCharacter, ScreenLabel.HasJobLine);
    private readonly MapHold _map = new();
    private readonly BandRead _mapBand = new();
    private readonly BandRead _nameBand = new();
    private readonly BandRead _statusBand = new();
    private string? _mapNote;
    private string? _nameNote;
    private StatusBarSheet? _status;

    public string CharacterName => _character.Value;
    public StatusBarSheet? StatusBar => _status;
    public string MapName => _map.Value;
    /// <summary>Map used to choose FM vs training. May move before <see cref="MapName"/> when the hold is locked to a hunting map.</summary>
    public string ModeMapName { get; private set; } = "";
    public int? MapId => _map.MapId;
    public PixelRect? Minimap { get; private set; }
    public PixelRect? MapBand { get; private set; }
    public PixelRect? NameBand { get; private set; }
    public string ChromeNote { get; private set; } = "";

    public void Reset()
    {
        _mapBand.Reset();
        _nameBand.Reset();
        _statusBand.Reset();
        _status = null;
    }

    public void Read(CapturedFrame frame)
    {
        ReadMap(frame);
        if (frame.Bgra.Length == 0 || frame.Height <= 0 || frame.Bgra.Length % frame.Height != 0) return;
        ReadStatusBar(frame);
        var nameLines = ReadBand(frame, NameBand, _nameBand, _character.Settled, ReadName);
        if (nameLines == null) return;
        var raw = string.Join("\n", nameLines);
        if (_character.Observe(raw))
        {
            TrainingSessionLog.Append([$"character name read as {_character.Value}"]);
        }
        else if (!_character.Settled)
        {
            Note(ref _nameNote, $"name plate read '{Flat(nameLines)}'; holding '{_character.Value}'");
        }
    }

    private void ReadStatusBar(CapturedFrame frame)
    {
        var box = StatusBarRegion.Bounds(frame.Width, frame.Height);
        var lines = ReadBand(frame, box, _statusBand, settled: false, ReadStatus);
        if (lines == null) return;
        var text = string.Join("\n", lines);
        var levelBox = StatusBarRegion.LevelBounds(frame.Width, frame.Height);
        var levelCrop = RegionCrop.CropRect(frame, levelBox.X, levelBox.Y, levelBox.Width, levelBox.Height);
        if (levelCrop.Ok && WindowsOcr.IsAvailable)
        {
            var levelLines = WindowsOcr.ReadLines(levelCrop.Bgra, levelCrop.Width, levelCrop.Height);
            if (levelLines.Count == 0)
            {
                levelLines = WindowsOcr.ReadLines(
                    GameFrame.PointScale(levelCrop.Bgra, levelCrop.Width, levelCrop.Height, levelCrop.Width * 3, levelCrop.Height * 3),
                    levelCrop.Width * 3,
                    levelCrop.Height * 3);
            }

            if (levelLines.Count > 0) text = string.Join("\n", levelLines) + "\n" + text;
        }

        var sheet = StatusBarText.Parse(text);
        if (!sheet.Succeeded) return;
        _status = sheet;
        TrainingSessionLog.Append([$"status plate {StatusBarText.Describe(sheet)}"]);
    }

    private static IReadOnlyList<string> ReadStatus(byte[] bgra, int width, int height)
    {
        var lines = WindowsOcr.ReadLines(bgra, width, height);
        if (StatusBarText.Parse(string.Join("\n", lines)).Succeeded) return lines;
        return WindowsOcr.ReadLines(
            GameFrame.PointScale(bgra, width, height, width * 2, height * 2),
            width * 2,
            height * 2);
    }

    private static string Flat(IReadOnlyList<string> lines) =>
        string.Join(" | ", lines.Select(line => line.Replace("\r", "").Replace("\n", " ").Trim()));

    private static void Note(ref string? last, string line)
    {
        if (line == last) return;
        last = line;
        TrainingSessionLog.Append([line]);
    }

    /// <summary>Minimap title only. The share calls this before choosing a scanner.</summary>
    public void ReadMap(CapturedFrame frame)
    {
        NameBand = CharacterNameRegion.Bounds(frame.Width, frame.Height);
        if (frame.Bgra.Length == 0 || frame.Height <= 0 || frame.Bgra.Length % frame.Height != 0)
        {
            Minimap = null;
            MapBand = null;
            ChromeNote = "";
            return;
        }

        var stride = frame.Bgra.Length / frame.Height;
        var chrome = UiChromeFinder.Find(frame.Bgra, frame.Width, frame.Height, stride);
        Minimap = chrome.Minimap;
        MapBand = chrome.MapTitle;
        ChromeNote = chrome.Note;
        var mapLines = ReadBand(frame, chrome.MapTitle, _mapBand, _map.Settled, MapTitleRead.Lines);
        if (mapLines != null)
        {
            var before = _map.Value;
            _map.Observe(MapTitle.Choose(mapLines));
            ModeMapName = ModeMapPicker.Resolve(ModeMapName, mapLines, before, _map.Value);
            if (!_map.Settled)
            {
                Note(ref _mapNote, $"map title read '{Flat(mapLines)}'; holding '{_map.Value}'");
            }
        }

        // Minimap OCR often misses Free Market while a hired merchant is open.
        // An open merchant is enough to run the FM scanner.
        if (!FmMapGate.Classify(ModeMapName).Ok)
        {
            PromoteFreeMarketFromMerchant(frame, chrome);
        }
    }

    private void PromoteFreeMarketFromMerchant(CapturedFrame frame, UiChrome chrome)
    {
        if (chrome.ShopTitle is not PixelRect title || title.IsEmpty) return;
        var crop = RegionCrop.CropRect(frame, title.X, title.Y, title.Width, title.Height);
        if (!crop.Ok || !WindowsOcr.IsAvailable) return;
        var lines = WindowsOcr.ReadLines(crop.Bgra, crop.Width, crop.Height);
        if (lines.Count == 0)
        {
            lines = WindowsOcr.ReadLines(
                GameFrame.PointScale(crop.Bgra, crop.Width, crop.Height, crop.Width * 2, crop.Height * 2),
                crop.Width * 2,
                crop.Height * 2);
        }

        var text = string.Join(" ", lines).ToLowerInvariant();
        text = Regex.Replace(text, @"[^a-z0-9]+", " ");
        if (!(text.Contains("hired merchant", StringComparison.Ordinal)
            || text.Contains("merchant", StringComparison.Ordinal)))
        {
            return;
        }

        var fallback = FmMapGate.Classify(_map.Value).Ok ? _map.Value : "Free Market";
        if (!string.Equals(ModeMapName, fallback, StringComparison.Ordinal))
        {
            Note(ref _mapNote, $"hired merchant open; using '{fallback}' for FM mode (minimap was '{ModeMapName}')");
        }

        ModeMapName = fallback;
    }

    /// <summary>The name plate text is small. Twice the size is tried when the plain read has no name.</summary>
    private static IReadOnlyList<string> ReadName(byte[] bgra, int width, int height)
    {
        var lines = WindowsOcr.ReadLines(bgra, width, height);
        if (ScreenLabel.AcceptCharacter(string.Join("\n", lines)) != null) return lines;
        return WindowsOcr.ReadLines(GameFrame.PointScale(bgra, width, height, width * 2, height * 2), width * 2, height * 2);
    }

    private static IReadOnlyList<string>? ReadBand(
        CapturedFrame frame,
        PixelRect? rect,
        BandRead gate,
        bool settled,
        Func<byte[], int, int, IReadOnlyList<string>> ocr)
    {
        if (rect is not PixelRect box || box.IsEmpty) return null;
        var crop = RegionCrop.CropRect(frame, box.X, box.Y, box.Width, box.Height);
        if (!crop.Ok) return null;
        if (!gate.ShouldRead(DropRegion.Hash(crop.Bgra), settled, Environment.TickCount64)) return null;
        if (!WindowsOcr.IsAvailable) return [];
        return ocr(crop.Bgra, crop.Width, crop.Height);
    }
}
