using System.IO;

namespace MarketNotes.Companion.Modes;

/// <summary>
/// Whether the debug drawer is open. The choice is kept on this PC.
/// </summary>
public static class DebugUiStore
{
    public static bool Load()
    {
        try
        {
            var path = Path();
            if (!File.Exists(path)) return true;
            return File.ReadAllText(path).Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Save(bool show)
    {
        try
        {
            var path = Path();
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, show ? "on" : "off");
        }
        catch
        {
            // A blocked settings folder still leaves the choice in effect for this run.
        }
    }

    private static string Path()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, "MarketNotes.Companion", "debug.txt");
    }
}
