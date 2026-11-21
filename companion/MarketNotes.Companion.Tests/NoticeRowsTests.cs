using MarketNotes.Companion.Farm;
using Xunit;

namespace MarketNotes.Companion.Tests;

public sealed class NoticeRowsTests
{
    [Fact]
    public void SplitMesoLineJoinsLeftToRight()
    {
        var lines = NoticeRows.Join(
        [
            new PlacedBox("(+284)", 190, 130, 236, 146),
            new PlacedBox("You have gained mesos", 20, 134, 186, 148),
        ]);

        var line = Assert.Single(lines);
        Assert.Equal("You have gained mesos (+284)", line.Text);
        Assert.Equal(130, line.Top);
    }

    [Fact]
    public void StackedLinesStaySeparate()
    {
        var lines = NoticeRows.Join(
        [
            new PlacedBox("full.", 230, 42, 262, 56),
            new PlacedBox("Your Etc inventory is", 20, 38, 226, 54),
            new PlacedBox("Your Etc inventory is full.", 20, 57, 262, 73),
            new PlacedBox("You have gained an item (Orange Mushroom Cap)", 20, 76, 400, 92),
        ]);

        Assert.Equal(
            ["Your Etc inventory is full.", "Your Etc inventory is full.", "You have gained an item (Orange Mushroom Cap)"],
            lines.Select(line => line.Text));
        Assert.Equal([38, 57, 76], lines.Select(line => line.Top));
    }

    [Fact]
    public void ANamePlateFusedOnTopKeepsTheNoticePosition()
    {
        var lines = NoticeRows.Join(
        [
            new PlacedBox("Your Etc inventory is full.", 20, 58, 262, 74),
            new PlacedBox("You have gaindarkonave gained mesos (+94)", 20, 71, 300, 112),
            new PlacedBox("You have gained mesos (+101)", 20, 114, 236, 130),
        ]);

        Assert.Equal([58, 96, 114], lines.Select(line => line.Top));
    }

    [Fact]
    public void Text_far_beside_a_notice_is_its_own_line()
    {
        var lines = NoticeRows.Join(
        [
            new PlacedBox("Meso: 3,369 (71/m)", 0, 58, 120, 74),
            new PlacedBox("You have gained an item in the Etc tab (Pounder)", 260, 58, 640, 74),
        ]);

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, line => line.Text == "You have gained an item in the Etc tab (Pounder)");
    }

    [Fact]
    public void A_box_spanning_several_battle_rows_does_not_join_them()
    {
        var lines = NoticeRows.Join(
        [
            new PlacedBox("Dmg:", 75, 80, 115, 98),
            new PlacedBox("740,562 (244/s)", 200, 80, 330, 98),
            new PlacedBox("05", 135, 82, 160, 140),
            new PlacedBox("Mob:", 75, 102, 115, 120),
            new PlacedBox("263 (5/m)", 230, 102, 330, 120),
            new PlacedBox("Exp:", 75, 124, 112, 142),
            new PlacedBox("38,160 (755/m)", 210, 124, 330, 142),
        ], splitGaps: false);

        Assert.Contains(lines, line => line.Text == "Dmg: 740,562 (244/s)");
        Assert.Contains(lines, line => line.Text == "Mob: 263 (5/m)");
        Assert.Contains(lines, line => line.Text == "Exp: 38,160 (755/m)");
    }
}
