using System.IO;
using System.Text;
using MarketNotes.Companion.Modes;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// Why a training visit was parked, resumed, ignored, or expired.
/// Written beside the loot OCR log so a disappearing drop list can be traced.
/// </summary>
public static class TrainingSessionLog
{
    private static readonly object Gate = new();
    private const int MaxBytes = 2_000_000;

    public static string FilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "MarketNotes.Companion", "training-session.log");
    }

    public static void Append(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || !DebugLog.Enabled) return;
        try
        {
            var block = new StringBuilder();
            var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var line in lines)
            {
                block.Append(stamp).Append(' ').AppendLine(line);
            }

            var path = FilePath();
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(path, block.ToString());
                Trim(path);
            }
        }
        catch
        {
            // A blocked log file does not change which visit is shown.
        }
    }

    private static void Trim(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;
        var text = File.ReadAllText(path);
        var keep = text[(text.Length / 2)..];
        var line = keep.IndexOf('\n');
        if (line >= 0) keep = keep[(line + 1)..];
        File.WriteAllText(path, keep);
    }
}
