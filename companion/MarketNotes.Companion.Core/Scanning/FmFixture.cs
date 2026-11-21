using System.Text.Json;
using MarketNotes.Companion.Data;

namespace MarketNotes.Companion.Scanning;

public static class FmFixture
{
    public static List<FmFrameInput> LoadFrames()
    {
        var path = DataFiles.Find("fm-shop.json") ?? throw new FileNotFoundException("data/fixtures/fm-shop.json was not found.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("frames", out var frames))
        {
            throw new InvalidOperationException("The FM sample has no frames.");
        }

        var list = JsonSerializer.Deserialize<List<FmFrameInput>>(frames.GetRawText(), DataFiles.JsonOptions)
            ?? throw new InvalidOperationException("The FM sample was empty.");
        foreach (var frame in list) frame.Rows ??= [];
        return list;
    }
}
