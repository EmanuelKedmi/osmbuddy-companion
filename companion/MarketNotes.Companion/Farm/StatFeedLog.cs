using System.IO;
using System.Text;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// Always-on, greppable trace for Stat (S) → /api/character/feed.
/// Writes a file and raises <see cref="LineWritten"/> so the companion UI can show the same lines live.
/// </summary>
public static class StatFeedLog
{
    private static readonly object Gate = new();
    private const int MaxBytes = 1_500_000;
    public const string Prefix = "[STAT-FEED]";

    /// <summary>Fired after each line is formatted (includes the [STAT-FEED] prefix, no timestamp).</summary>
    public static event Action<string>? LineWritten;

    public static string FilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "MarketNotes.Companion", "stat-feed.log");
    }

    public static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var line = message.Trim();
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            line = Prefix + " " + line;

        try
        {
            var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var block = stamp + " " + line + Environment.NewLine;
            var path = FilePath();
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(path, block, Encoding.UTF8);
                Trim(path);
            }
        }
        catch
        {
            // A blocked log file must not stop the feed.
        }

        // Also land in the training session log when that sink is writing.
        TrainingSessionLog.Append([line]);

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // A UI listener fault must not stop the feed.
        }
    }

    private static void Trim(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;
        var text = File.ReadAllText(path);
        var keep = text[(text.Length / 2)..];
        var cut = keep.IndexOf('\n');
        if (cut >= 0) keep = keep[(cut + 1)..];
        File.WriteAllText(path, keep);
    }
}
