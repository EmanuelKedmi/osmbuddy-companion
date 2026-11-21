using MarketNotes.Companion.Scanning;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class IdentityHoldTests
{
    [Fact]
    public void Split_name_is_joined_and_the_job_line_is_skipped()
    {
        Assert.Equal("ExtraSin", ScreenLabel.AcceptCharacter("Extra Sin"));
        Assert.Equal("ExtraSin", ScreenLabel.AcceptCharacter("Assassin\nExtraSin"));
        Assert.Equal("ExtraSin", ScreenLabel.AcceptCharacter("Assassin\nExtra Sin"));
        Assert.True(ScreenLabel.HasJobLine("Assassin\nExtraSin"));
        Assert.False(ScreenLabel.HasJobLine("ssin\nExtra"));
    }

    [Fact]
    public void Cursor_over_the_name_plate_does_not_replace_the_name()
    {
        var who = new LabelHold(ScreenLabel.AcceptCharacter, ScreenLabel.HasJobLine);
        Assert.False(who.Observe("Assassin\nExtraSin", 0));
        Assert.True(who.Observe("Assassin\nExtraSin", 2_000));
        Assert.True(who.Settled);

        Assert.False(who.Observe("Extra", 4_000));
        Assert.False(who.Observe("Extra", 6_000));
        Assert.False(who.Observe("aSin", 8_000));
        Assert.True(who.Settled);

        Assert.False(who.Observe("ssin", 10_000));
        Assert.False(who.Observe("ssin", 12_000));
        Assert.False(who.Settled);
        Assert.Equal("ExtraSin", who.Value);

        Assert.False(who.Observe("Assassin\nExtraSin", 14_000));
        Assert.True(who.Settled);
        Assert.Equal("ExtraSin", who.Value);
    }

    [Fact]
    public void A_different_character_with_its_job_line_replaces_the_name()
    {
        var who = new LabelHold(ScreenLabel.AcceptCharacter, ScreenLabel.HasJobLine);
        who.Observe("Assassin\nExtraSin", 0);
        who.Observe("Assassin\nExtraSin", 1);
        Assert.False(who.Observe("Wizard\nExtraCold", 2_000));
        Assert.True(who.Observe("Wizard\nExtraCold", 4_000));
        Assert.Equal("ExtraCold", who.Value);
    }

    [Fact]
    public void Near_miss_name_keeps_the_longer_read()
    {
        var who = new LabelHold(ScreenLabel.AcceptCharacter, ScreenLabel.HasJobLine);
        who.Observe("Sniper\nMonkeDKof", 0);
        who.Observe("Sniper\nMonkeDKof", 1);
        Assert.Equal("MonkeDKof", who.Value);

        Assert.True(who.Observe("Sniper\nMonkeyDKof", 2_000));
        Assert.Equal("MonkeyDKof", who.Value);

        Assert.False(who.Observe("Sniper\nMonkeDKof", 4_000));
        Assert.False(who.Observe("Sniper\nMonkeDKof", 6_000));
        Assert.Equal("MonkeyDKof", who.Value);
    }

    [Fact]
    public void Ledger_name_match_prefers_the_saved_spelling()
    {
        var names = new[] { "MonkeyDKof", "ProbeOnly" };
        Assert.Equal("MonkeyDKof", LedgerNameMatch.Prefer("MonkeDKof", names));
        Assert.Equal("MonkeyDKof", LedgerNameMatch.Prefer("monkeydkof", names));
        Assert.Equal("ProbeOnly", LedgerNameMatch.Prefer("ProbeOnly", names));
        Assert.Equal("BrandNew", LedgerNameMatch.Prefer("BrandNew", names));
        Assert.True(ScreenLabel.NamesClose("Neho", "NehoraiSan"));
        Assert.Equal(
            "NehoraiSan",
            LedgerNameMatch.Prefer("Neho", new[] { "MonkeyDKof", "NehoraiSan" }));
        Assert.Equal(
            "MonkeyDKof",
            LedgerNameMatch.Prefer("MonkeDKof", new[] { "MonkeDKof", "MonkeyDKof" }));
    }

    [Fact]
    public void A_name_without_its_job_line_replaces_only_after_it_holds_for_a_while()
    {
        var who = new LabelHold(ScreenLabel.AcceptCharacter, ScreenLabel.HasJobLine);
        who.Observe("Assassin\nExtraSin", 0);
        who.Observe("Assassin\nExtraSin", 1);
        Assert.False(who.Observe("ExtraCold", 2_000));
        Assert.False(who.Observe("ExtraCold", 10_000));
        Assert.True(who.Observe("ExtraCold", 2_000 + LabelHold.WeakReplaceMs));
        Assert.Equal("ExtraCold", who.Value);
    }

    [Fact]
    public void Town_line_alone_is_held_but_not_settled_until_the_map_line_reads()
    {
        var map = new MapHold();
        var town = MapTitle.Choose(["Ludibrium"]);
        var full = MapTitle.Choose(["Ludibrium", "Whirlpool of Time"]);
        Assert.NotNull(full);
        Assert.Equal("Whirlpool of Time", full.Value.Name);
        Assert.False(full.Value.StreetOnly);

        map.Observe(town);
        map.Observe(town);
        Assert.Equal("Ludibrium", map.Value);
        Assert.False(map.Settled);

        Assert.True(map.Observe(full));
        Assert.Equal("Whirlpool of Time", map.Value);
        Assert.True(map.Settled);

        Assert.False(map.Observe(town));
        Assert.Equal("Whirlpool of Time", map.Value);
    }

    [Theory]
    [InlineData("Ludibrium | Whirlpo I of Time")]
    [InlineData("Ludibrium | Whirlpoo f Time")]
    [InlineData("Ludibr | Whirlpo of Time")]
    [InlineData("ibrium | WI irlpool of Time")]
    [InlineData("L :hrium | Whi pool of Time")]
    public void Title_with_a_letter_or_two_missing_still_names_the_map(string read)
    {
        var choice = MapTitle.Choose(read.Split(" | "));
        Assert.NotNull(choice);
        Assert.Equal("Whirlpool of Time", choice.Value.Name);
        Assert.True(choice.Value.FromCatalog);
        Assert.False(choice.Value.StreetOnly);
    }

    [Fact]
    public void Near_match_does_not_cross_a_numeral()
    {
        Assert.Null(MapCatalog.Match("Ludibrium"));
        var one = MapCatalog.Match("Henesys Hunting Ground I");
        Assert.NotNull(one);
        var near = MapCatalog.Match("Henesys Huntng Ground II");
        if (near != null) Assert.NotEqual(one.Value.Id, near.Value.Id);
    }

    [Fact]
    public void Catalog_name_on_the_upper_line_with_an_unread_line_under_it_is_street_only()
    {
        var street = MapTitle.Choose(["Mushroom Garden", "Qworn Plibet Varst"]);
        Assert.NotNull(street);
        Assert.Equal("Mushroom Garden", street.Value.Name);
        Assert.True(street.Value.StreetOnly);

        var same = MapTitle.Choose(["Mushroom Garden", "Mushroom Garden"]);
        Assert.NotNull(same);
        Assert.False(same.Value.StreetOnly);

        var map = new MapHold();
        Assert.True(map.Observe(MapTitle.Choose(["Korean Folk Town", "Entrance to Black Mountain"])));
        Assert.True(map.Settled);
        Assert.False(map.Observe(street));
        Assert.False(map.Observe(street));
        Assert.Equal("Entrance to Black Mountain", map.Value);
        Assert.False(map.Settled);
    }
}
