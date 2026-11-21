using MarketNotes.Companion.Farm;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class KillsPerDropTests
{
    [Fact]
    public void Shows_one_item_per_n_kills()
    {
        Assert.Equal("1 / 47", KillsPerDrop.Format(1, 47));
        Assert.Equal("1 / 10", KillsPerDrop.Format(1, 10));
        Assert.Equal("1 / 11", KillsPerDrop.Format(2, 21));
        Assert.Equal("1 / 47 kills", KillsPerDrop.Format(1, 47, withUnit: true));
    }

    [Fact]
    public void Keeps_one_decimal_below_ten()
    {
        Assert.Equal("1 / 2.0", KillsPerDrop.Format(1, 2));
        Assert.Equal("1 / 2.5", KillsPerDrop.Format(4, 10));
        Assert.Equal("1 / 9.5", KillsPerDrop.Format(2, 19));
        Assert.Equal("1 / 10", KillsPerDrop.Format(100, 996));
    }

    [Fact]
    public void Shows_units_per_kill_when_n_is_below_one()
    {
        Assert.Equal("2.0 / kill", KillsPerDrop.Format(2, 1));
        Assert.Equal("2.5 / kill", KillsPerDrop.Format(5, 2));
        Assert.Equal("250 / kill", KillsPerDrop.Format(50_000, 200));
        Assert.Equal("2.0 / kill", KillsPerDrop.Format(2, 1, withUnit: true));
    }

    [Fact]
    public void Shows_a_dash_for_zero_drops_or_kills()
    {
        Assert.Equal("—", KillsPerDrop.Format(0, 47));
        Assert.Equal("—", KillsPerDrop.Format(1, 0));
        Assert.Equal("—", KillsPerDrop.FromPerKill(null));
        Assert.Equal("—", KillsPerDrop.FromPerKill(0));
        Assert.Equal("—", KillsPerDrop.FromPerKill(double.PositiveInfinity));
        Assert.Null(KillsPerDrop.Value(0, 47));
        Assert.Null(KillsPerDrop.Value(1, 0));
    }

    [Fact]
    public void Sorts_by_n_so_a_more_common_drop_is_smaller()
    {
        var common = KillsPerDrop.Value(50, 1);
        var uncommon = KillsPerDrop.Value(1, 47);
        Assert.Equal(0.02, common);
        Assert.Equal(47, uncommon);
        Assert.True(common < uncommon);
    }

    [Fact]
    public void Training_report_uses_kills_per_drop()
    {
        var session = new TrainingSession();
        session.ApplyBattle(new BattleSnapshot { Kills = 47, DurationMs = 3_600_000 });
        session.Drops.Add(new LootStack { ItemName = "Orange Mushroom Cap", Quantity = 1 });
        session.Drops.Add(new LootStack { ItemName = "Mesos", Quantity = 94 });

        var text = TrainingReport.Format(session, LootPoolSnapshot.FromSession(session));

        Assert.Contains("1 / 47 kills", text);
        Assert.Contains("2.0 / kill", text);
        Assert.DoesNotContain(" per kill", text);

        var empty = new TrainingSession();
        empty.ApplyBattle(new BattleSnapshot { Kills = 0 });
        empty.Drops.Add(new LootStack { ItemName = "Nothing", Quantity = 3 });
        var quiet = TrainingReport.Format(empty, LootPoolSnapshot.FromSession(empty));
        Assert.Contains("—", quiet);
        Assert.DoesNotContain("Infinity", quiet);
        Assert.DoesNotContain("NaN", quiet);
    }
}
