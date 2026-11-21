using System.Globalization;
using System.IO;
using System.Text;
using MarketNotes.Companion.Modes;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// One line per battle-window pass so a wrong kill count can be read after the fact.
/// The website token is never written here.
/// </summary>
public static class BattleDebugLog
{
    private static readonly object Gate = new();
    private const int MaxLines = 4000;
    private const int KeepLines = 3000;
    private static int _lines = -1;

    public static string FilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "MarketNotes.Companion", "battle-debug.log");
    }

    public static void Skipped()
    {
        Write(Stamp() + " skipped");
    }

    public static void Read(IReadOnlyList<string> lines, BattleSnapshot? parsed, string? mapName, int? shownKills, string use, bool fromColumns = false)
    {
        var ocr = string.Join(" | ", lines.Select(line => line.Replace("\r", "").Replace("\n", " ").Trim()).Where(line => line.Length > 0));
        var text = new StringBuilder();
        text.Append(Stamp());
        text.Append(fromColumns ? " mode=columns" : " mode=panel");
        text.Append(" ocr=").Append(Quote(ocr));
        text.Append(" kills=").Append(Num(parsed?.Kills));
        text.Append(" damage=").Append(Num(parsed?.Damage));
        text.Append(" exp=").Append(Num(parsed?.Exp));
        text.Append(" meso=").Append(Num(parsed?.Meso));
        text.Append(" elapsed=").Append(Num(parsed?.DurationMs));
        text.Append(" map=").Append(Quote(mapName?.Trim() ?? ""));
        text.Append(" shown=").Append(shownKills?.ToString(CultureInfo.InvariantCulture) ?? "-");
        if (parsed?.FrameChecked == true)
        {
            text.Append(parsed.FrameGood ? " gate=good" : " gate=rejected");
            if (!parsed.FrameGood && parsed.FrameReason.Length > 0) text.Append(" reason=").Append(Quote(parsed.FrameReason));
        }
        else
        {
            text.Append(" gate=unchecked");
        }

        text.Append(' ');
        text.Append(use is "kept" or "corrected" or "ignored" || use == "rejected" ? use : "ignored");
        Write(text.ToString());
    }

    private static string Stamp() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Num(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static void Write(string line)
    {
        if (!DebugLog.Enabled) return;
        try
        {
            var path = FilePath();
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                if (_lines < 0) _lines = File.Exists(path) ? File.ReadAllLines(path).Length : 0;
                File.AppendAllText(path, line + Environment.NewLine);
                _lines++;
                if (_lines <= MaxLines) return;
                var all = File.ReadAllLines(path);
                var keep = all.Length <= KeepLines ? all : all[^KeepLines..];
                File.WriteAllLines(path, keep);
                _lines = keep.Length;
            }
        }
        catch
        {
            // A blocked log file does not change which kills are counted.
        }
    }
}
