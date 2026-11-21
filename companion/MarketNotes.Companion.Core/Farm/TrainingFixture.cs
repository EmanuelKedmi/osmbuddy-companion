using System.Text.Json;
using MarketNotes.Companion.Data;

namespace MarketNotes.Companion.Farm;

public sealed class TrainingFixtureFile
{
    public int MapId { get; set; }
    public string MapName { get; set; } = "";
    public TrainingBattle Battle { get; set; } = new();
    public List<NoticeFrame> Frames { get; set; } = [];
}

public sealed class TrainingBattle
{
    public string Text { get; set; } = "";
}

public static class TrainingFixture
{
    public static TrainingFixtureFile Load()
    {
        var path = DataFiles.Find("training-sample.json")
            ?? throw new FileNotFoundException("data/fixtures/training-sample.json was not found.");
        var file = JsonSerializer.Deserialize<TrainingFixtureFile>(File.ReadAllText(path), DataFiles.JsonOptions)
            ?? throw new InvalidOperationException("The training sample was empty.");
        file.Battle ??= new TrainingBattle();
        file.Frames ??= [];
        return file;
    }
}
