using System.IO;
using System.Text;
using MarketNotes.Companion.Modes;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// Every loot-corner OCR read, including lines the parser drops.
/// The file is append-only so a sitting can be read after the fact.
/// </summary>
public static class LootOcrLog
{
    private static readonly object Gate = new();
    private const int MaxBytes = 8_000_000;

    public static string FilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "MarketNotes.Companion", "loot-ocr.log");
    }

    public static void Append(
        int frameWidth,
        int frameHeight,
        int cropWidth,
        int cropHeight,
        string? hash,
        string? mapName,
        int? mapId,
        IReadOnlyList<string> lines,
        IReadOnlyList<string> words,
        IReadOnlyList<int>? tops = null,
        long? atMs = null)
    {
        if (!DebugLog.Enabled) return;
        try
        {
            var at = atMs is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : DateTimeOffset.Now;
            var block = new StringBuilder();
            block.Append("--- ").Append(at.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            block.Append(" frame ").Append(frameWidth).Append('x').Append(frameHeight);
            block.Append(" crop ").Append(cropWidth).Append('x').Append(cropHeight);
            block.Append(" hash ").Append(string.IsNullOrWhiteSpace(hash) ? "-" : hash);
            if (tops != null && tops.Count == lines.Count) block.Append(" tops ").Append(tops.Count == 0 ? "-" : string.Join(",", tops));
            block.Append(" map ").Append(string.IsNullOrWhiteSpace(mapName) ? "?" : mapName.Trim());
            block.Append(" (").Append(mapId?.ToString() ?? "?").AppendLine(")");
            if (lines.Count == 0)
            {
                block.AppendLine("(no text)");
            }
            else
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    block.Append("RAW ").AppendLine(line);
                    if (i < words.Count && words[i].Length > 0 && !string.Equals(words[i], line, StringComparison.Ordinal))
                    {
                        block.Append("WORDS ").AppendLine(words[i]);
                    }

                    block.Append("    ").AppendLine(DropLines.Explain(line, mapId, mapName));
                }
            }

            block.AppendLine();
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
            // A blocked log file does not change what the scanner counts.
        }
    }

    private static void Trim(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;
        var text = File.ReadAllText(path);
        if (text.Length <= MaxBytes) return;
        var start = text.Length - MaxBytes * 3 / 4;
        var mark = text.IndexOf("\n--- ", start, StringComparison.Ordinal);
        File.WriteAllText(path, mark >= 0 ? text[(mark + 1)..] : text[start..]);
    }
}
