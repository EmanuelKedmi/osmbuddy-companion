using System.Text.Json;
using MarketNotes.Companion.Data;

namespace MarketNotes.Companion.Scanning;

public sealed class StatWindowFixture
{
    public string Text { get; set; } = "";
    public bool Sample { get; set; }
    public bool Ocr { get; set; }
}

public sealed class SkillWindowFixture
{
    public string Text { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public bool Sample { get; set; }
    public bool Ocr { get; set; }
}

public static class CharacterSheetFixture
{
    public static StatWindowFixture LoadStat()
    {
        var path = DataFiles.Find("stat-window.json")
            ?? throw new FileNotFoundException("data/fixtures/stat-window.json was not found.");
        return JsonSerializer.Deserialize<StatWindowFixture>(File.ReadAllText(path), DataFiles.JsonOptions)
            ?? throw new InvalidOperationException("The stat window sample was empty.");
    }

    public static SkillWindowFixture LoadSkill()
    {
        var path = DataFiles.Find("skill-window.json")
            ?? throw new FileNotFoundException("data/fixtures/skill-window.json was not found.");
        return JsonSerializer.Deserialize<SkillWindowFixture>(File.ReadAllText(path), DataFiles.JsonOptions)
            ?? throw new InvalidOperationException("The skill window sample was empty.");
    }
}
