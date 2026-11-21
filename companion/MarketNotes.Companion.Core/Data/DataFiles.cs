using System.Text.Json;

namespace MarketNotes.Companion.Data;

/// <summary>
/// Finds the JSON the companion ships beside the executable, or the repo
/// copies when a developer runs from the source tree.
/// </summary>
public static class DataFiles
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static JsonSerializerOptions JsonOptions => ReadOptions;

    public static string Require(string fileName)
    {
        return Find(fileName) ?? throw new FileNotFoundException($"Could not find {fileName}.");
    }

    public static string? Find(string fileName)
    {
        var relatives = new[]
        {
            Path.Combine("fixtures", fileName),
            Path.Combine("data", fileName),
            Path.Combine("data", "fixtures", fileName),
        };
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var dir = new DirectoryInfo(start);
            for (var depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                foreach (var relative in relatives)
                {
                    var path = Path.Combine(dir.FullName, relative);
                    if (File.Exists(path)) return path;
                }
            }
        }

        return null;
    }
}
