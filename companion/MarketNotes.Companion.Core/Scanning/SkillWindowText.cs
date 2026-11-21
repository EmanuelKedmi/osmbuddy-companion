using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Scanning;

public sealed class SkillRank
{
    public required string Id { get; init; }
    public required int Level { get; init; }
    public required string Label { get; init; }
}

public sealed class SkillSheet
{
    public IReadOnlyList<SkillRank> Skills { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];
    public bool Succeeded { get; init; }

    public string Signature(string? name)
    {
        var skills = string.Join(",", Skills.Select(skill => skill.Id + ":" + skill.Level.ToString(CultureInfo.InvariantCulture)));
        var skipped = string.Join(",", Skipped);
        return (name ?? "") + "|" + skills + "|" + skipped;
    }
}

/// <summary>
/// Reads skill levels the planner already understands. A skill that is not
/// in that list, or a skill whose level is not a number in range, is skipped.
/// Nothing here writes a 0 for a skill the window did not show.
/// </summary>
public static class SkillWindowText
{
    private const int MaxText = 4000;

    private readonly record struct SkillDef(string Name, string Id, int Max);

    private static readonly SkillDef[] Skills = BuildSkills();

    private static readonly HashSet<string> Chrome = new(StringComparer.OrdinalIgnoreCase)
    {
        "skill", "skills", "sp", "ap", "ok", "close", "detail", "job", "name", "remaining", "points",
        "beginner", "warrior", "magician", "bowman", "thief", "pirate", "wizard", "cleric", "priest",
        "bishop", "mage", "hunter", "crossbowman", "ranger", "sniper", "assassin", "bandit", "hermit",
        "page", "fighter", "spearman", "crusader", "hero", "paladin", "knight", "brawler", "gunslinger",
        "marauder", "outlaw", "buccaneer", "corsair", "marksman",
    };

    public static SkillSheet Parse(string? text)
    {
        var source = (text ?? "").Replace("\r", "");
        if (source.Length > MaxText) source = source[..MaxText];
        var lines = source
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var used = new bool[lines.Length];
        var skills = new List<SkillRank>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            if (used[i]) continue;
            foreach (var skill in Skills)
            {
                if (TryLevel(lines[i], skill.Name, skill.Max, out var level))
                {
                    used[i] = true;
                    Keep(skills, skipped, seen, skill, level, lines[i]);
                    break;
                }

                if (NameIs(lines[i], skill.Name) && i + 1 < lines.Length && !used[i + 1] && LevelToken(lines[i + 1], skill.Max, out var next))
                {
                    used[i] = true;
                    used[i + 1] = true;
                    Keep(skills, skipped, seen, skill, next, lines[i]);
                    break;
                }
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (used[i]) continue;
            if (TryUnknown(lines[i], out var label))
            {
                used[i] = true;
                AddSkip(skipped, label);
                continue;
            }

            if (i + 1 < lines.Length && !used[i + 1] && Title(lines[i]) && LevelToken(lines[i + 1], 30, out _) && !Chrome.Contains(Plain(lines[i])))
            {
                used[i] = true;
                used[i + 1] = true;
                AddSkip(skipped, Plain(lines[i]));
            }
        }

        var title = lines.Any(line => line.Equals("Skill", StringComparison.OrdinalIgnoreCase) || line.Equals("Skills", StringComparison.OrdinalIgnoreCase));
        return new SkillSheet
        {
            Skills = skills,
            Skipped = skipped,
            Succeeded = skills.Count > 0 || (title && skipped.Count > 0),
        };
    }

    public static string Describe(SkillSheet sheet, string? name, bool sample)
    {
        var lead = sample
            ? "Sample. OCR did not run. These skills were not read from a game window."
            : "Skill window.";
        if (!sheet.Succeeded)
        {
            return lead + " The text was not a readable skill window, so nothing was added.";
        }

        var who = string.IsNullOrWhiteSpace(name) ? "The character name was not read." : "Character " + name.Trim() + ".";
        var known = sheet.Skills.Count == 0
            ? "No planner skill had a readable level."
            : "Skills: " + string.Join(", ", sheet.Skills.Select(skill => skill.Label + " " + skill.Level.ToString(CultureInfo.InvariantCulture))) + ".";
        var missed = sheet.Skipped.Count == 0
            ? ""
            : " Skipped " + string.Join(", ", sheet.Skipped) + ".";
        return lead + " " + who + " " + known + missed;
    }

    private static SkillDef[] BuildSkills()
    {
        var list = new List<SkillDef>
        {
            new("power strike", "power-strike", 20),
            new("slash blast", "slash-blast", 20),
            new("brandish", "brandish", 30),
            new("crusher", "crusher", 30),
            new("dragon roar", "dragon-roar", 20),
            new("blast", "blast", 30),
            new("energy bolt", "energy-bolt", 20),
            new("magic claw", "magic-claw", 20),
            new("fire arrow", "fire-arrow", 30),
            new("cold beam", "cold-beam", 30),
            new("holy arrow", "holy-arrow", 30),
            new("poison breath", "poison-breath", 30),
            new("thunder bolt", "thunder-bolt", 30),
            new("explosion", "explosion", 30),
            new("blizzard", "blizzard", 30),
            new("arrow blow", "arrow-blow", 20),
            new("double shot (gun)", "double-shot-gun", 20),
            new("double shot(gun)", "double-shot-gun", 20),
            new("double shot", "double-shot", 20),
            new("power knock-back", "power-knockback", 20),
            new("power knockback", "power-knockback", 20),
            new("power knock back", "power-knockback", 20),
            new("arrow bomb", "arrow-bomb", 30),
            new("iron arrow", "iron-arrow", 30),
            new("strafe", "strafe", 30),
            new("hurricane", "hurricane", 30),
            new("lucky seven", "lucky-seven", 20),
            new("double stab", "double-stab", 20),
            new("drain", "drain", 30),
            new("savage blow", "savage-blow", 30),
            new("triple throw", "triple-throw", 30),
            new("flash fist", "flash-fist", 20),
            new("somersault kick", "somersault", 30),
            new("somersault", "somersault", 30),
            new("barrage", "barrage", 30),
            new("burst fire", "burst-fire", 30),
            new("rapid fire", "rapid-fire", 30),
            new("sword mastery", "mastery", 20),
            new("axe mastery", "mastery", 20),
            new("bw mastery", "mastery", 20),
            new("blunt mastery", "mastery", 20),
            new("spear mastery", "mastery", 20),
            new("pole arm mastery", "mastery", 20),
            new("polearm mastery", "mastery", 20),
            new("bow mastery", "mastery", 20),
            new("crossbow mastery", "mastery", 20),
            new("claw mastery", "mastery", 20),
            new("dagger mastery", "mastery", 20),
            new("knuckle mastery", "mastery", 20),
            new("gun mastery", "mastery", 20),
            new("mastery", "mastery", 20),
            new("booster", "booster", 20),
            new("rage", "rage", 20),
            new("critical shot", "crit", 30),
            new("critical throw", "crit", 30),
            new("focus", "focus", 20),
            new("nimble body", "nimble", 20),
            new("shadow partner", "shadow", 30),
            new("meso up", "meso", 20),
            new("meditation", "meditation", 20),
        };
        return list.OrderByDescending(skill => skill.Name.Length).ToArray();
    }

    private static void Keep(List<SkillRank> skills, List<string> skipped, HashSet<string> seen, SkillDef skill, int? level, string line)
    {
        var label = LabelOf(line, skill.Name);
        if (level == null)
        {
            AddSkip(skipped, label);
            return;
        }

        if (!seen.Add(skill.Id))
        {
            var index = skills.FindIndex(row => row.Id == skill.Id);
            if (index >= 0) skills[index] = new SkillRank { Id = skill.Id, Level = level.Value, Label = label };
            return;
        }

        skills.Add(new SkillRank { Id = skill.Id, Level = level.Value, Label = label });
    }

    private static bool TryLevel(string line, string skillName, int max, out int? level)
    {
        level = null;
        var norm = Norm(line);
        if (!norm.StartsWith(skillName, StringComparison.Ordinal)) return false;
        if (norm.Length != skillName.Length && norm[skillName.Length] != ' ') return false;
        var rest = norm[skillName.Length..].Trim();
        if (rest.Length == 0) return false;
        if (!LevelToken(rest, max, out var parsed)) return false;
        level = parsed;
        return true;
    }

    private static bool NameIs(string line, string skillName) => Norm(line) == skillName;

    private static bool LevelToken(string line, int max, out int? level)
    {
        level = null;
        var match = Regex.Match(line.Trim(), @"^(?:[Ll]v\.?\s*)?(\d{1,2})(?:\s*/\s*\d{1,2})?$");
        if (!match.Success) return false;
        var parsed = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (parsed > max) 
        {
            level = null;
            return true;
        }

        level = parsed;
        return true;
    }

    private static bool TryUnknown(string line, out string label)
    {
        label = "";
        var match = Regex.Match(line.Trim(), @"^([A-Za-z][A-Za-z' \-]{2,40}?)\s+(?:[Ll]v\.?\s*)?(\d{1,2})(?:\s*/\s*\d{1,2})?$");
        if (!match.Success) return false;
        label = match.Groups[1].Value.Trim();
        var name = label;
        if (Chrome.Contains(name)) return false;
        if (Skills.Any(skill => skill.Name == Norm(name))) return false;
        return true;
    }

    private static bool Title(string line)
    {
        var plain = Plain(line);
        if (plain.Length < 3 || plain.Length > 40) return false;
        return Regex.IsMatch(plain, @"^[A-Za-z][A-Za-z' \-]+$");
    }

    private static string LabelOf(string line, string skillName)
    {
        var trimmed = line.Trim();
        var index = trimmed.IndexOf(' ');
        var head = index < 0 ? trimmed : trimmed[..index];
        if (Norm(trimmed).StartsWith(skillName, StringComparison.Ordinal))
        {
            var cut = Math.Min(trimmed.Length, skillName.Length);
            var words = trimmed[..cut].Trim();
            if (words.Length > 0) return words;
        }

        return head;
    }

    private static string Plain(string line) => Regex.Replace(line.Trim(), @"\s+", " ");

    private static string Norm(string line)
    {
        var text = line.Trim().ToLowerInvariant();
        text = text.Replace('–', '-').Replace('—', '-');
        return Regex.Replace(text, @"\s+", " ");
    }

    private static void AddSkip(List<string> skipped, string label)
    {
        if (skipped.Any(row => row.Equals(label, StringComparison.OrdinalIgnoreCase))) return;
        skipped.Add(label);
    }
}
