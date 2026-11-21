namespace MarketNotes.Companion.Launch;

/// <summary>
/// Whether this PC should keep the companion as the ngm:// handler.
/// A missing file means off.
/// </summary>
public static class NgmHijackPreference
{
    public static bool IsEnabled(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            if (!File.Exists(file)) return false;
            return File.ReadAllText(file).Trim() == "1";
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(file, enabled ? "1" : "0");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "MarketNotes.Companion", "ngm-hijack.txt");
    }
}
