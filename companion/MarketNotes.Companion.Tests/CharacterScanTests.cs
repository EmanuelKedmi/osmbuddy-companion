using System.Text.Json;
using MarketNotes.Companion.Scanning;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class CharacterScanTests
{
    [Fact]
    public void Stat_sample_reads_the_window_and_not_mp_as_a_guess()
    {
        var fixture = CharacterSheetFixture.LoadStat();
        Assert.False(fixture.Ocr);
        Assert.True(fixture.Sample);

        var sheet = StatWindowText.Parse(fixture.Text);
        Assert.True(sheet.Succeeded);
        Assert.Equal("Aran", sheet.Name);
        Assert.Equal("warrior", sheet.Branch);
        Assert.Equal(47, sheet.Level);
        Assert.Equal(185, sheet.Str);
        Assert.Equal(42, sheet.Dex);
        Assert.Equal(20, sheet.Int);
        Assert.Equal(18, sheet.Luk);
        Assert.Equal(2450, sheet.MaxHp);
        Assert.Equal(820, sheet.MaxMp);
        Assert.Equal(112, sheet.WeaponAttack);
        Assert.Equal(15, sheet.MagicAttack);
        Assert.Equal(148, sheet.Accuracy);
        Assert.Equal(86, sheet.WeaponDef);
        Assert.Equal(31, sheet.MagicDef);
    }

    [Fact]
    public void Stat_window_accepts_a_value_on_the_next_line_and_a_split_job()
    {
        var sheet = StatWindowText.Parse("""
            Stat
            Aran
            Arch Mage (Fire, Poison)
            Level
            55
            HP
            1,100 / 1,400
            STR
            4
            DEX
            4
            INT
            320
            LUK
            4
            Magic Attack
            129
            """);

        Assert.True(sheet.Succeeded);
        Assert.Equal("Aran", sheet.Name);
        Assert.Equal("magician", sheet.Branch);
        Assert.Equal(55, sheet.Level);
        Assert.Equal(1400, sheet.MaxHp);
        Assert.Equal(320, sheet.Int);
        Assert.Equal(129, sheet.MagicAttack);
        Assert.Null(sheet.WeaponAttack);
        Assert.Null(sheet.Accuracy);
    }

    [Fact]
    public void Unreadable_text_and_a_title_without_stats_add_nothing()
    {
        var battle = StatWindowText.Parse("Battle Analysis\nKills: 120\nTime elapsed: 15:00\nDamage: 1,844,000");
        Assert.False(battle.Succeeded);
        Assert.Null(battle.Name);
        Assert.Null(battle.Level);
        Assert.Null(battle.Str);

        var title = StatWindowText.Parse("Stat\nAran");
        Assert.False(title.Succeeded);
        Assert.Null(title.Str);

        var chrome = StatWindowText.Parse("Stat\nWarrior\nLevel 47\nSTR 40\nDEX 20\nINT 4");
        Assert.True(chrome.Succeeded);
        Assert.Null(chrome.Name);
        Assert.NotEqual("Stat", chrome.Name);
    }

    [Fact]
    public void A_nameless_stat_sheet_keeps_the_numbers_and_no_name()
    {
        var sheet = StatWindowText.Parse("Level 12\nSTR 40\nDEX 20\nINT 4\nLUK 4");
        Assert.True(sheet.Succeeded);
        Assert.Null(sheet.Name);
        Assert.Equal(12, sheet.Level);
        Assert.Equal(40, sheet.Str);
    }

    [Theory]
    [InlineData("LeveI 74", 74)]
    [InlineData("Lvl 74", 74)]
    [InlineData("Lv.74", 74)]
    [InlineData("Lv 74", 74)]
    [InlineData("Lovel 74", 74)]
    public void OCR_mangled_level_labels_still_read_the_number(string levelLine, int expected)
    {
        var sheet = StatWindowText.Parse($"Stat\nSniper\n{levelLine}\nSTR 66\nDEX 339\nINT 11\nLUK 11");
        Assert.True(sheet.Succeeded);
        Assert.Equal(expected, sheet.Level);
    }

    [Fact]
    public void Status_plate_reads_level_and_max_hp()
    {
        var sheet = StatusBarText.Parse("LV 74 Sniper\n1602/2637\n307/1659\nEXP 1738133");
        Assert.True(sheet.Succeeded);
        Assert.Equal(74, sheet.Level);
        Assert.Equal("bowman", sheet.Branch);
        Assert.Equal(2637, sheet.MaxHp);
        Assert.Equal(1659, sheet.MaxMp);
    }

    [Fact]
    public void Worlds_character_stat_reads_bonus_totals_without_labels()
    {
        var text = """
            CHRRRCTER STRT
            JOB
            FRME
            MonkeyDKof
            Sniper
            79
            2749 / 2749
            762 / 1733
            1179607 (44%)
            4
            74 (61+13)
            403 (361+42)
            12 (4+8)
            12 (4+8)
            RTTRCK
            DEF.
            MRGIC
            MRGIC DEF.
            RCCURRC'f
            JUMP
            151
            18
            27
            287
            116
            112%
            108%
            """;
        var sheet = StatWindowText.Parse(text);
        Assert.True(sheet.Succeeded);
        Assert.Equal("MonkeyDKof", sheet.Name);
        Assert.Equal("bowman", sheet.Branch);
        Assert.Equal(79, sheet.Level);
        Assert.Equal(74, sheet.Str);
        Assert.Equal(403, sheet.Dex);
        Assert.Equal(12, sheet.Int);
        Assert.Equal(12, sheet.Luk);
        Assert.Equal(2749, sheet.MaxHp);
        Assert.Equal(1733, sheet.MaxMp);
        Assert.Equal(151, sheet.WeaponAttack);
        Assert.Equal(18, sheet.WeaponDef);
        Assert.Equal(27, sheet.MagicAttack);
        Assert.Equal(287, sheet.Accuracy);
    }

    [Fact]
    public void Worlds_character_stat_three_bonus_totals_still_fill_str_dex_int()
    {
        // Live OCR often drops the LUK line as "ISTRI / Luv" with no digits.
        var text = """
            CHRRRCTER STRT
            JOB
            FRME
            MonkeyDKof
            Sniper
            81
            2822 / 2822
            431 / 1794
            419497 (14%)
            3
            77 (63+14)
            412 (369+43)
            13 (4+9)
            ISTRI
            Luv
            """;
        var sheet = StatWindowText.Parse(text);
        Assert.True(sheet.Succeeded);
        Assert.Equal("MonkeyDKof", sheet.Name);
        Assert.Equal(81, sheet.Level);
        Assert.Equal(77, sheet.Str);
        Assert.Equal(412, sheet.Dex);
        Assert.Equal(13, sheet.Int);
        Assert.Null(sheet.Luk);
        Assert.Equal(2822, sheet.MaxHp);
    }

    [Fact]
    public void Worlds_rogue_sheet_reads_name_level_and_branch()
    {
        // Live OCR 2026-11-19: Rogue was missing from Jobs, so name+level stayed null
        // and CharacterLive refused to POST (no character create/update).
        var text = """
            CHRRRCTER STRT
            JOB
            LEVEL
            GUILD
            ERP.
            FFME
            NehoraiSan
            Rogue
            24
            882/882
            12570 (46%)
            RBILITY POINT
            12 (4+8)
            34 (25+9)
            12 (4+8)
            121 (112+9)
            """;
        var sheet = StatWindowText.Parse(text);
        Assert.True(sheet.Succeeded);
        Assert.Equal("NehoraiSan", sheet.Name);
        Assert.Equal("thief", sheet.Branch);
        Assert.Equal(24, sheet.Level);
        Assert.Equal(12, sheet.Str);
        Assert.Equal(34, sheet.Dex);
        Assert.Equal(12, sheet.Int);
        Assert.Equal(121, sheet.Luk);
        Assert.Equal(882, sheet.MaxHp);
    }

    [Fact]
    public void Speed_and_a_scroll_line_are_not_weapon_attack()
    {
        var sheet = StatWindowText.Parse("Level 30\nSTR 80\nINT 4\nLUK 4\nSpeed 140\nScroll for Gloves for DEX 10%");
        Assert.True(sheet.Succeeded);
        Assert.Null(sheet.WeaponAttack);
        Assert.Equal(80, sheet.Str);
        Assert.Null(sheet.Dex);
    }

    [Fact]
    public void Skill_sample_reads_planner_skills_and_skips_the_rest()
    {
        var fixture = CharacterSheetFixture.LoadSkill();
        Assert.False(fixture.Ocr);
        Assert.Equal("Aran", fixture.CharacterName);

        var sheet = SkillWindowText.Parse(fixture.Text);
        Assert.True(sheet.Succeeded);
        Assert.Equal(20, Level(sheet, "power-strike"));
        Assert.Equal(10, Level(sheet, "slash-blast"));
        Assert.Equal(20, Level(sheet, "mastery"));
        Assert.Equal(16, Level(sheet, "booster"));
        Assert.Equal(12, Level(sheet, "rage"));
        Assert.Contains(sheet.Skipped, name => name.Equals("Final Attack", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sheet.Skipped, name => name.Equals("Haste", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(5, sheet.Skills.Count);
    }

    [Fact]
    public void An_unreadable_skill_level_does_not_replace_a_level_that_was_read()
    {
        var sheet = SkillWindowText.Parse("Skill\nPower Strike\n20\nPower Strike 99\nHaste");
        Assert.True(sheet.Succeeded);
        Assert.Equal(20, Level(sheet, "power-strike"));
        Assert.DoesNotContain(sheet.Skills, skill => skill.Level == 0 || skill.Level == 99);
    }

    [Fact]
    public void A_skill_name_without_a_level_is_not_zero()
    {
        var sheet = SkillWindowText.Parse("Skill\nRage\nBooster\n16");
        Assert.True(sheet.Succeeded);
        Assert.Null(sheet.Skills.SingleOrDefault(skill => skill.Id == "rage")?.Level);
        Assert.Equal(16, Level(sheet, "booster"));
    }

    [Fact]
    public void Gun_double_shot_does_not_become_the_bow_skill()
    {
        var gun = SkillWindowText.Parse("Skill\nDouble Shot (gun) 15");
        Assert.Equal("double-shot-gun", Assert.Single(gun.Skills).Id);
        Assert.Equal(15, gun.Skills[0].Level);

        var bow = SkillWindowText.Parse("Skill\nDouble Shot 18");
        Assert.Equal("double-shot", Assert.Single(bow.Skills).Id);
        Assert.Equal(18, bow.Skills[0].Level);
    }

    [Fact]
    public void Battle_text_is_not_a_skill_window()
    {
        var sheet = SkillWindowText.Parse("Battle Analysis\nKills: 120\nHaste");
        Assert.False(sheet.Succeeded);
        Assert.Empty(sheet.Skills);
    }

    [Fact]
    public void A_framed_window_is_found_for_the_stat_and_skill_reader()
    {
        const int width = 640;
        const int height = 420;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 80;
            pixels[i + 2] = 30;
            pixels[i + 3] = 255;
        }

        DrawEdge(pixels, width, 80, 60, 220, 200);
        var frames = UiChromeFinder.Frames(pixels, width, height, width * 4);
        var frame = Assert.Single(frames);
        Assert.InRange(frame.X, 70, 90);
        Assert.InRange(frame.Y, 50, 70);
        Assert.InRange(frame.Width, 200, 240);
        Assert.InRange(frame.Height, 180, 220);
    }

    [Fact]
    public void A_stat_payload_uses_the_sheet_field_name_int()
    {
        var sheet = StatWindowText.Parse(CharacterSheetFixture.LoadStat().Text);
        var payload = new
        {
            kind = "stat",
            name = sheet.Name,
            branch = sheet.Branch,
            level = sheet.Level,
            str = sheet.Str,
            dex = sheet.Dex,
            @int = sheet.Int,
            luk = sheet.Luk,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(20, doc.RootElement.GetProperty("int").GetInt32());
        Assert.Equal("Aran", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("warrior", doc.RootElement.GetProperty("branch").GetString());
    }

    private static int Level(SkillSheet sheet, string id)
    {
        var skill = Assert.Single(sheet.Skills, row => row.Id == id);
        return skill.Level;
    }

    private static void DrawEdge(byte[] pixels, int stridePixels, int x, int y, int width, int height)
    {
        for (var col = 0; col < width; col++)
        {
            Set(pixels, stridePixels, x + col, y, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
            Set(pixels, stridePixels, x + col, y + 1, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
            Set(pixels, stridePixels, x + col, y + height - 1, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
            Set(pixels, stridePixels, x + col, y + height - 2, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
        }
    }

    private static void Set(byte[] pixels, int stridePixels, int x, int y, byte r, byte g, byte b)
    {
        var index = (y * stridePixels + x) * 4;
        pixels[index] = b;
        pixels[index + 1] = g;
        pixels[index + 2] = r;
        pixels[index + 3] = 255;
    }
}
