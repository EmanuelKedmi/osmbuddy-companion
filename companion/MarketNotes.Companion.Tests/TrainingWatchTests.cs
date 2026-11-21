using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Scanning;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class TrainingWatchTests
{
    [Fact]
    public void Entering_a_map_while_analysis_is_running_subtracts_the_baseline()
    {
        var watch = new TrainingWatch();
        var baseline = Panel(kills: 100, exp: 5_000, damage: 9_000, elapsedMs: 242_000, meso: 1_000, attacks: 400);
        watch.Observe(Signal(AnalysisSignal.Running, null, baseline, 1_000));
        Assert.Null(watch.OpenSession);

        var opened = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            baseline,
            atMs: 2_000,
            character: "ExtraCold",
            mapId: 222010000));
        var session = opened.Shown;
        Assert.NotNull(session);
        Assert.True(session.Open);
        Assert.True(session.JoinedMidAnalysis);
        Assert.Equal("ExtraCold", session.CharacterName);
        Assert.Equal("Entrance to Black Mountain", session.MapName);
        Assert.Equal(0, session.Kills);
        Assert.Equal(0, session.Exp);
        Assert.Equal(0, session.Damage);
        Assert.Equal(0, session.Attacks);
        Assert.Equal(0, session.Meso);
        Assert.Equal(0, session.DurationMs);

        var later = Panel(kills: 130, exp: 8_000, damage: 12_000, elapsedMs: 300_000, meso: 1_500, attacks: 520);
        watch.Observe(Signal(AnalysisSignal.Running, "Entrance to Black Mountain", later, atMs: 60_000, mapId: 222010000));
        Assert.Equal(30, session.Kills);
        Assert.Equal(3_000, session.Exp);
        Assert.Equal(3_000, session.Damage);
        Assert.Equal(120, session.Attacks);
        Assert.Equal(500, session.Meso);
        Assert.Equal(58_000, session.DurationMs);
    }

    [Fact]
    public void Loot_waits_until_the_map_is_a_catalog_map()
    {
        var watch = new TrainingWatch();
        var waiting = watch.Observe(Signal(
            AnalysisSignal.Running,
            null,
            Panel(4, 80, 200, 5_000, 12),
            atMs: 1_000,
            notice: Frame("loot", "You have gained mesos (+203).", "You have gained an item in the Etc tab (Cursed Doll")));
        Assert.Null(waiting.Shown);
        Assert.Null(watch.OpenSession);

        var unread = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Monkey ForeWll",
            Panel(6, 90, 220, 8_000, 20),
            atMs: 2_000,
            notice: Frame("misread", "You have gained mesos (+161).")));
        Assert.NotNull(unread.Shown);
        Assert.Empty(unread.Shown.Drops);

        var named = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            atMs: 3_000,
            mapId: 104030001,
            notice: Frame("named", "You have gained an item in the Etc tab (Cursed Doll")));
        Assert.Equal("Mushroom Garden", named.Shown!.MapName);
        Assert.Contains(named.Shown.Drops, drop => drop.ItemName == "Cursed Doll");
        Assert.DoesNotContain(named.Shown.Drops, drop => drop.ItemName == "Mesos");
    }

    [Fact]
    public void Starting_analysis_on_the_map_does_not_subtract_a_previous_hunt()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Old Hunt", Panel(900, 20_000, 40_000, 600_000, 8_000), atMs: 1_000));
        watch.Observe(Signal(AnalysisSignal.Running, "Old Hunt", Panel(950, 21_000, 42_000, 660_000, 8_400), atMs: 61_000));
        Assert.Equal(50, watch.Shown!.Kills);

        var held = watch.Observe(Signal(AnalysisSignal.Idle, "Old Hunt", atMs: 62_000));
        Assert.True(held.Shown!.Open);
        Assert.Null(held.JustClosed);
        Assert.Equal(50, held.Shown.Kills);

        var started = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(4, 80, 200, 5_000, 12), atMs: 70_000));
        Assert.True(started.Shown!.Open);
        Assert.False(started.Shown.JoinedMidAnalysis);
        Assert.Equal(4, started.Shown.Kills);
        Assert.Equal(80, started.Shown.Exp);
        Assert.Equal(200, started.Shown.Damage);
        Assert.Equal(12, started.Shown.Meso);
        Assert.NotEqual(started.Shown, started.JustClosed);
    }

    [Fact]
    public void A_map_change_closes_the_visit_and_starts_a_new_baseline()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Entrance to Black Mountain", Panel(100, 5_000, 9_000, 242_000, 1_000), atMs: 1_000));
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(130, 8_000, 12_000, 300_000, 1_500),
            atMs: 60_000,
            notice: Frame("a", "You have gained an Orange Potion.")));
        Assert.Equal(30, watch.Shown!.Kills);
        Assert.Equal(1, Assert.Single(watch.Shown.Drops).Quantity);

        var changed = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(160, 9_000, 13_000, 330_000, 1_800),
            atMs: 90_000,
            notice: Frame("b", "You have gained a Red Potion.")));
        var closed = changed.JustClosed;
        Assert.NotNull(closed);
        Assert.False(closed.Open);
        Assert.Equal("Entrance to Black Mountain", closed.MapName);
        Assert.Equal(60, closed.Kills);
        Assert.Equal("Orange Potion", Assert.Single(closed.Drops).ItemName);

        Assert.True(changed.Shown!.Open);
        Assert.Equal("Mushroom Garden", changed.Shown.MapName);
        Assert.Equal(0, changed.Shown.Kills);
        Assert.Equal("Red Potion", Assert.Single(changed.Shown.Drops).ItemName);
        Assert.NotEqual(closed.FeedKey, changed.Shown.FeedKey);
    }

    [Fact]
    public void A_mob_total_read_after_changing_maps_is_not_a_new_kill()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(0, 0, 10_000, 60_000, 800),
            atMs: 1_000,
            mapId: 222010000));
        Assert.Equal(0, watch.Shown!.Kills);

        var changed = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(0, 1_000, 15_719, 77_000, 852),
            atMs: 70_000,
            mapId: 104030001));
        Assert.Equal("Mushroom Garden", changed.Shown!.MapName);
        Assert.Equal(0, changed.Shown.Kills);
        Assert.Equal(0, changed.JustClosed!.Kills);
        Assert.Equal(5_719, changed.JustClosed.Damage);

        var read = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(509, 1_560, 15_719, 192_000, 900),
            atMs: 90_000,
            mapId: 104030001));
        Assert.Equal(0, read.Shown!.Kills);
        Assert.Equal(0, read.Shown.Damage);
        Assert.Equal(560, read.Shown.Exp);
    }

    [Fact]
    public void A_lower_battle_cell_does_not_replace_the_panel()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(40, 500, 12_000, 90_000, 200),
            atMs: 1_000,
            mapId: 222010000));
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(0, 100, 0, 10_000, 0),
            atMs: 2_000,
            mapId: 222010000));
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal(0, watch.Shown.Damage);

        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(55, 800, 14_000, 100_000, 300),
            atMs: 3_000,
            mapId: 222010000));
        Assert.Equal(15, watch.Shown!.Kills);
        Assert.Equal(2_000, watch.Shown.Damage);
    }

    [Fact]
    public void Kills_on_the_same_map_still_count_after_a_zero_mob_cell()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(0, 0, 10_000, 60_000, 100),
            atMs: 1_000,
            mapId: 222010000));
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(40, 500, 12_000, 90_000, 200),
            atMs: 30_000,
            mapId: 222010000));
        Assert.Equal(40, watch.Shown!.Kills);
        Assert.Equal(2_000, watch.Shown.Damage);
    }

    [Fact]
    public void Returning_to_a_map_shows_that_maps_drops_again()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Entrance to Black Mountain", Panel(100, 5_000, 9_000, 242_000, 1_000), atMs: 1_000, mapId: 222010000));
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(130, 8_000, 12_000, 300_000, 1_500),
            atMs: 60_000,
            mapId: 222010000,
            notice: Frame("a", "You have gained an Orange Potion.")));
        var away = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(160, 9_000, 13_000, 330_000, 1_800),
            atMs: 90_000,
            mapId: 104030001,
            notice: Frame("b", "You have gained a Red Potion.")));
        var parked = away.JustClosed;
        Assert.Equal("Red Potion", Assert.Single(away.Shown!.Drops).ItemName);

        var back = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(200, 11_000, 16_000, 400_000, 2_200),
            atMs: 120_000,
            mapId: 222010000));
        Assert.Same(parked, back.Shown);
        Assert.True(back.Shown!.Open);
        Assert.Equal(60, back.Shown.Kills);
        Assert.Equal("Orange Potion", Assert.Single(back.Shown.Drops).ItemName);
        Assert.Contains(watch.DrainTrace(), line => line.StartsWith("resumed Entrance to Black Mountain", StringComparison.Ordinal));
    }

    [Fact]
    public void A_misread_map_does_not_replace_the_drop_list()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(4, 80, 200, 5_000, 12),
            atMs: 1_000,
            mapId: 104030001,
            notice: Frame("a", "You have gained an Orange Potion.")));
        var session = watch.Shown;

        var misread = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Monkey ForeWll",
            Panel(8, 100, 240, 8_000, 20),
            atMs: 2_000,
            notice: Frame("b", "You have gained mesos (+40).")));
        Assert.Same(session, misread.Shown);
        Assert.Equal("Mushroom Garden", misread.Shown!.MapName);
        Assert.Equal(2, misread.Shown.Drops.Count);
        Assert.Contains(watch.DrainTrace(), line => line.StartsWith("ignored misread 'Monkey ForeWll'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_parked_map_expires_after_fifteen_minutes()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(10, 10, 10, 10_000, 1),
            atMs: 1_000,
            mapId: 222010000,
            notice: Frame("a", "You have gained an Orange Potion.")));
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(12, 12, 12, 12_000, 2),
            atMs: 2_000,
            mapId: 104030001));

        var expired = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Entrance to Black Mountain",
            Panel(20, 20, 20, 20_000, 3),
            atMs: 2_000 + TrainingWatch.IdleKeepMs + 1,
            mapId: 222010000));
        Assert.Equal("Entrance to Black Mountain", expired.Shown!.MapName);
        Assert.Empty(expired.Shown.Drops);
        Assert.Contains(watch.DrainTrace(), line => line.StartsWith("expired Entrance to Black Mountain", StringComparison.Ordinal));
    }

    [Fact]
    public void Stopping_for_a_free_market_map_closes_the_visit_without_opening_another()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(10, 100, 200, 5_000, 20), atMs: 1_000));
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(20, 200, 400, 10_000, 40), atMs: 5_000));
        Assert.True(watch.OpenSession!.Open);

        var stopped = watch.Stop();
        Assert.NotNull(stopped);
        Assert.False(stopped.Open);
        Assert.Equal("Mushroom Garden", stopped.MapName);
        Assert.Null(watch.OpenSession);
        Assert.Equal(stopped, watch.Shown);
    }

    [Fact]
    public void Stopping_analysis_closes_the_visit_and_keeps_the_drops()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Idle, "Mushroom Garden", atMs: 1_000));
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(2, 10, 20, 1_000, 0),
            atMs: 2_000,
            notice: Frame("a", "You have gained an Orange Potion.")));
        var held = watch.Observe(Signal(AnalysisSignal.Idle, "Mushroom Garden", Panel(3, 15, 25, 4_000, 5), atMs: 5_000));
        Assert.True(held.Shown!.Open);
        Assert.Null(held.JustClosed);
        Assert.Equal(3, held.Shown.Kills);

        var stopped = watch.Observe(Signal(
            AnalysisSignal.Idle,
            "Mushroom Garden",
            Panel(3, 15, 25, 4_000, 5),
            atMs: 5_000 + TrainingWatch.IdleGraceMs));
        Assert.Null(watch.OpenSession);
        Assert.False(stopped.Shown!.Open);
        Assert.Equal(3, stopped.Shown.Kills);
        Assert.Equal("Orange Potion", Assert.Single(stopped.Shown.Drops).ItemName);
        Assert.NotNull(stopped.JustClosed);

        var gone = new TrainingWatch();
        gone.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(8, 10, 10, 10_000, 1), atMs: 1_000));
        var flickered = gone.Observe(Signal(AnalysisSignal.Absent, "Mushroom Garden", atMs: 2_000));
        Assert.True(flickered.Shown!.Open);
        var absent = gone.Observe(Signal(AnalysisSignal.Absent, "Mushroom Garden", atMs: 2_000 + TrainingWatch.IdleGraceMs));
        Assert.False(absent.Shown!.Open);
        Assert.Equal(0, absent.Shown.Kills);
        Assert.NotNull(absent.JustClosed);
    }

    [Fact]
    public void A_share_that_joins_a_running_panel_starts_at_zero_kills()
    {
        var watch = new TrainingWatch();
        var waiting = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            atMs: 1_000,
            mapId: 104030001));
        Assert.NotNull(waiting.Shown);
        Assert.Equal(0, waiting.Shown!.Kills);

        var anchored = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(51, 8_000, 8_234, 90_000, 3_000),
            atMs: 1_500,
            mapId: 104030001));
        Assert.Same(waiting.Shown, anchored.Shown);
        Assert.Equal(51, anchored.Shown!.Kills);
        Assert.Equal(8_000, anchored.Shown.Exp);
        Assert.Equal(8_234, anchored.Shown.Damage);

        var later = watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(53, 8_400, 8_500, 95_000, 3_100),
            atMs: 4_000,
            mapId: 104030001));
        Assert.Equal(53, later.Shown!.Kills);
        Assert.Equal(8_400, later.Shown.Exp);
        Assert.Equal(8_500, later.Shown.Damage);
    }

    [Fact]
    public void A_short_idle_does_not_split_the_visit()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(10, 100, 100, 20_000, 40), atMs: 2_000));
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(12, 140, 180, 25_000, 55), atMs: 6_000));
        var held = watch.Observe(Signal(AnalysisSignal.Idle, "Mushroom Garden", Panel(12, 140, 180, 25_000, 55), atMs: 7_000));
        Assert.True(held.Shown!.Open);
        Assert.Null(held.JustClosed);

        var back = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(14, 160, 200, 28_000, 60), atMs: 12_000));
        Assert.Same(held.Shown, back.Shown);
        Assert.True(back.Shown!.Open);
        Assert.Null(back.JustClosed);
        Assert.Equal(4, back.Shown.Kills);
        Assert.Contains(watch.DrainTrace(), line => line.StartsWith("kept Mushroom Garden open", StringComparison.Ordinal));
    }

    [Fact]
    public void The_loot_table_stays_separate_for_each_character_on_a_map()
    {
        var path = Path.Combine(Path.GetTempPath(), "osm-pool-" + Guid.NewGuid().ToString("N") + ".json");
        var pool = new LootPoolStore(path);
        var cold = Visit("ExtraCold", 104030001, "Mushroom Garden", 1, 20, "Orange Potion", 2);
        var sin = Visit("ExtraSin", 104030001, "Mushroom Garden", 1, 50, "Orange Potion", 9);
        var other = Visit("ExtraCold", 222010000, "Entrance to Black Mountain", 1, 8, "Blue Potion", 1);
        pool.ReplaceSession(cold);
        pool.ReplaceSession(sin);
        pool.ReplaceSession(other);

        var live = Visit("ExtraCold", 104030001, "Mushroom Garden", 2, 5, "Orange Potion", 1);
        var totals = pool.Combined("ExtraCold", 104030001, live);
        Assert.Equal(25, totals.Kills);
        Assert.Equal(3, totals.Quantity("Orange Potion"));
        Assert.Equal(0, totals.Quantity("Blue Potion"));

        pool.ReplaceSession(live);
        var newer = Visit("ExtraCold", 104030001, "Mushroom Garden", 2, 6, "Orange Potion", 2);
        var again = pool.Combined("ExtraCold", 104030001, newer);
        Assert.Equal(26, again.Kills);
        Assert.Equal(4, again.Quantity("Orange Potion"));

        var sinTotals = pool.Combined("ExtraSin", 104030001, null);
        Assert.Equal(50, sinTotals.Kills);
        Assert.Equal(9, sinTotals.Quantity("Orange Potion"));
        File.Delete(path);
    }

    [Fact]
    public void One_uncertain_battle_frame_does_not_close_the_visit()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(10, 100, 100, 20_000, 40), atMs: 1_000));
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Panel(12, 140, 180, 25_000, 55), atMs: 6_000));
        var missed = watch.Observe(Signal(AnalysisSignal.Uncertain, "Mushroom Garden", atMs: 7_000));
        Assert.True(missed.Shown!.Open);
        Assert.Null(missed.JustClosed);
        Assert.Equal(2, missed.Shown.Kills);
        Assert.Equal(15, missed.Shown.Meso);
    }

    [Fact]
    public void Confirmed_drops_sum_and_garbled_lines_stay_out()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Idle, "Mushroom Garden", atMs: 1_000));
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            Panel(1, 1, 1, 1_000, 0),
            atMs: 2_000,
            notice: Frame("a", "You have gained an Orange Potion.", "E*traCoId", "You have gained a Raccoon Mask.", "You have gained exper.")));
        watch.Observe(Signal(
            AnalysisSignal.Running,
            "Mushroom Garden",
            atMs: 3_000,
            notice: Frame("b", "You have gained Orange Potion x2.", "You have gained mesos (+40).")));

        var drops = watch.Shown!.Drops.OrderBy(row => row.ItemName).ToArray();
        Assert.Equal(2, drops.Length);
        Assert.Equal("Mesos", drops[0].ItemName);
        Assert.Equal(40, drops[0].Quantity);
        Assert.Equal("Orange Potion", drops[1].ItemName);
        Assert.Equal(3, drops[1].Quantity);
        Assert.DoesNotContain(watch.Shown.Drops, row => row.ItemName.Contains("Raccoon", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(watch.Shown.Drops, row => row.ItemName.Contains("tra", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Character_and_map_holds_ignore_one_bad_read()
    {
        var who = new LabelHold(ScreenLabel.AcceptCharacter);
        Assert.False(who.Observe("ExtraCold"));
        Assert.True(who.Observe("ExtraCold"));
        Assert.False(who.Observe("E*traCoId"));
        Assert.False(who.Observe("You have gained exper."));
        Assert.False(who.Observe("Wizard (I/L)"));
        Assert.Equal("ExtraCold", who.Value);
        Assert.False(who.Observe("Wizard (I/L)\nExtraCold"));
        Assert.Equal("ExtraCold", who.Value);

        var map = new MapHold();
        Assert.False(map.Observe(MapTitle.Choose(["Korean Folk Town"])));
        Assert.Equal("", map.Value);
        Assert.True(map.Observe(MapTitle.Choose(["Korean Folk Town", "Entrance to Black Mountain"])));
        Assert.Equal("Entrance to Black Mountain", map.Value);
        Assert.Equal(222010000, map.MapId);
        Assert.False(map.Observe(MapTitle.Choose(["Entr@nce"])));
        Assert.False(map.Observe(MapTitle.Choose(["Korean Folk Town"])));
        Assert.Equal("Entrance to Black Mountain", map.Value);
        Assert.True(map.Observe(MapTitle.Choose(["Mushroom Garden"])));
        Assert.Equal("Mushroom Garden", map.Value);
        Assert.NotNull(map.MapId);
    }

    [Fact]
    public void A_share_stamps_the_first_good_mob_read_and_counts_later_kills()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", atMs: 1_000, mapId: 104030001));

        var first = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(36), atMs: 2_000, mapId: 104030001));
        Assert.Equal(0, first.Shown!.Kills);

        var same = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(36), atMs: 2_500, mapId: 104030001));
        Assert.Equal(0, same.Shown!.Kills);

        var next = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(40), atMs: 3_000, mapId: 104030001));
        Assert.Equal(4, next.Shown!.Kills);
    }

    [Fact]
    public void A_map_change_stamps_the_first_good_mob_read_on_the_new_map()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Entrance to Black Mountain", atMs: 1_000, mapId: 222010000));
        var changed = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", atMs: 2_000, mapId: 104030001));
        Assert.Equal("Mushroom Garden", changed.Shown!.MapName);
        Assert.Equal(0, changed.Shown.Kills);

        var first = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(500), atMs: 3_000, mapId: 104030001));
        Assert.Equal(0, first.Shown!.Kills);

        var next = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(503), atMs: 4_000, mapId: 104030001));
        Assert.Equal(3, next.Shown!.Kills);
    }

    [Fact]
    public void A_new_analysis_stamps_a_zero_mob_read_and_counts_from_there()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Idle, "Mushroom Garden", atMs: 1_000, mapId: 104030001));

        var first = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(0), atMs: 2_000, mapId: 104030001));
        Assert.Equal(0, first.Shown!.Kills);

        var next = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(5), atMs: 3_000, mapId: 104030001));
        Assert.Equal(5, next.Shown!.Kills);
    }

    [Fact]
    public void A_bad_millions_read_does_not_stamp_or_move_the_kill_counter()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", atMs: 1_000, mapId: 104030001));
        var bad = BattleText.Parse("Mob: 2.16M (440/s) | Exp: 475 (5/m)");
        Assert.False(bad.FrameGood);

        var ignored = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", bad, atMs: 2_000, mapId: 104030001));
        Assert.Equal(0, ignored.Shown!.Kills);

        var stamped = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(36), atMs: 3_000, mapId: 104030001));
        Assert.Equal(0, stamped.Shown!.Kills);

        var next = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(40), atMs: 4_000, mapId: 104030001));
        Assert.Equal(4, next.Shown!.Kills);
    }

    [Fact]
    public void A_small_dip_does_not_lower_the_session_kill_count()
    {
        var watch = new TrainingWatch();
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", atMs: 1_000, mapId: 104030001));
        watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(36), atMs: 2_000, mapId: 104030001));
        var up = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(40), atMs: 3_000, mapId: 104030001));
        Assert.Equal(4, up.Shown!.Kills);

        var dip = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(38), atMs: 4_000, mapId: 104030001));
        Assert.Equal(4, dip.Shown!.Kills);

        var more = watch.Observe(Signal(AnalysisSignal.Running, "Mushroom Garden", Good(42), atMs: 5_000, mapId: 104030001));
        Assert.Equal(6, more.Shown!.Kills);
    }

    private static TrainingSession Visit(string character, int mapId, string map, int generation, int kills, string item, long quantity)
    {
        var session = new TrainingSession();
        session.BeginLive(character, mapId, map, generation, false, new BattleSnapshot(), 1_000);
        session.ApplyBattle(new BattleSnapshot { Kills = kills, DurationMs = 60_000, Text = "panel" });
        session.Drops.Add(new LootStack { ItemName = item, Quantity = quantity, Notices = 1 });
        return session;
    }

    private static TrainingSignal Signal(
        AnalysisSignal analysis,
        string? map,
        BattleSnapshot? battle = null,
        long atMs = 0,
        string? character = null,
        int? mapId = null,
        NoticeFrame? notice = null)
    {
        return new TrainingSignal
        {
            Analysis = analysis,
            MapName = map,
            MapId = mapId,
            CharacterName = character,
            Battle = battle,
            Notices = notice == null ? null : [notice],
            AtMs = atMs,
        };
    }

    private static NoticeFrame Frame(string hash, params string[] lines)
    {
        return new NoticeFrame { RegionHash = hash, Lines = lines.ToList() };
    }

    private static BattleSnapshot Good(int kills)
    {
        return new BattleSnapshot
        {
            Kills = kills,
            KillsFromMob = true,
            Exp = 8_000 + kills,
            Damage = 80_000 + kills,
            DurationMs = 60_000L + kills * 1_000L,
            Meso = 3_000 + kills,
            FrameChecked = true,
            FrameGood = true,
            Text = "good",
        };
    }

    private static BattleSnapshot Panel(int kills, long exp, long damage, long elapsedMs, long meso, long? attacks = null)
    {
        return new BattleSnapshot
        {
            Kills = kills,
            Exp = exp,
            Damage = damage,
            DurationMs = elapsedMs,
            Meso = meso,
            Attacks = attacks,
            Text = "panel",
        };
    }
}
