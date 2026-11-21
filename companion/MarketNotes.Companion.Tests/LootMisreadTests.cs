using MarketNotes.Companion.Farm;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class LootMisreadTests
{
    [Theory]
    [InlineData("You have gained nnesos (f 181)", 181)]
    [InlineData("You have gained nnesos (f 210)", 210)]
    [InlineData("You have gained rnesos (+77)", 77)]
    public void Meso_read_as_nn_or_rn_still_counts(string line, int amount)
    {
        var drop = DropLines.Parse(line);
        Assert.NotNull(drop);
        Assert.Equal("Mesos", drop.ItemName);
        Assert.Equal(amount, drop.Quantity);
    }

    [Theory]
    [InlineData("You have gaincd an item in thc Etc tab (Cactus' Flower)", "Cactus' Flower")]
    [InlineData("an item in the Etc tab (Cactus' Flowcr)", "Cactus' Flower")]
    [InlineData("have gained an item in the Use tab (Arrow for Crossbow)", "Arrow for Crossbow")]
    [InlineData("You have gained an itenn in the Etc tab (Cactus Thorn)", "Cactus Thorn")]
    public void Pickup_with_a_garbled_start_still_counts(string line, string item)
    {
        var drop = DropLines.Parse(line);
        Assert.NotNull(drop);
        Assert.Equal(item, drop.ItemName);
    }

    [Theory]
    [InlineData("Your Use inventory is full.")]
    [InlineData("Persian Cat")]
    [InlineData("Mark of Omnia V")]
    [InlineData("You have gained experience (+82)")]
    public void Notices_that_are_not_pickups_stay_out(string line)
    {
        Assert.Null(DropLines.Parse(line));
    }

    [Fact]
    public void Two_letters_off_counts_only_against_this_maps_drops()
    {
        const string line = "You hsve gained an item in the Etc tab (Cactus' Flouver)";
        Assert.Null(DropLines.Parse(line));
        var drop = DropLines.Parse(line, 260010201, "Royal Catthus Desert");
        Assert.NotNull(drop);
        Assert.Equal("Cactus' Flower", drop.ItemName);
    }

    [Theory]
    [InlineData("You have gained [nesos (4153)", 153)]
    [InlineData("have '-ained nnesos (1221:", 221)]
    [InlineData("You have gained rnesos (+225", 225)]
    [InlineData("You have gained mesos (+1,250)", 1250)]
    public void Meso_amount_with_the_plus_misread_is_repaired(string line, int amount)
    {
        var drop = DropLines.Parse(line);
        Assert.NotNull(drop);
        Assert.Equal(amount, drop.Quantity);
    }

    [Theory]
    [InlineData("You have gained [nesos (+22c")]
    [InlineData("You have gained [nesos (f21C)")]
    [InlineData("You have gained [nesos (4 1411")]
    public void Meso_amount_with_a_misread_digit_is_not_trusted(string line)
    {
        Assert.Null(DropLines.Parse(line));
    }

    [Fact]
    public void Garbled_numeral_does_not_pick_a_tier()
    {
        var drop = DropLines.Parse("You have gained an item in the Etc tab (Haunted Essence IJ");
        Assert.True(drop == null || drop.ItemName != "Haunted Essence III");
    }

    [Fact]
    public void Meso_read_with_a_bracket_for_the_m_still_counts()
    {
        var drop = DropLines.Parse("You have gained [nesos (+141)");
        Assert.NotNull(drop);
        Assert.Equal("Mesos", drop.ItemName);
        Assert.Equal(141, drop.Quantity);
    }

    [Theory]
    [InlineData("You have gained an the Etc tab (Table Cloc(")]
    [InlineData("You have gained an lhe Etc tab (Tablc Clock J'")]
    [InlineData("You have gained an itern in the Etc tab (Table Clock)")]
    public void Table_clock_with_a_broken_bracket_still_counts(string line)
    {
        var drop = DropLines.Parse(line);
        Assert.NotNull(drop);
        Assert.Equal("Table Clock", drop.ItemName);
        Assert.Equal(1, drop.Quantity);
    }
}
