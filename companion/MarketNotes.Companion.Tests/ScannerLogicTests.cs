using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using MarketNotes.Companion.Data;
using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Scanning;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class ScannerLogicTests
{
    [Fact]
    public void Map_gate_accepts_a_free_market_room_and_rejects_mushroom_garden()
    {
        var room = FmMapGate.Classify("Free Market<1>");
        Assert.True(room.Ok);
        Assert.Null(room.ChannelFromName);
        Assert.Equal("Free Market<1>", room.MatchedName);

        var alias = FmMapGate.Classify("FM 1");
        Assert.True(alias.Ok);
        Assert.Null(alias.ChannelFromName);
        Assert.Equal("Free Market<1>", alias.MatchedName);

        var channel = FmMapGate.Classify("FM Channel 1");
        Assert.True(channel.Ok);
        Assert.Equal(1, channel.ChannelFromName);

        Assert.True(FmMapGate.Classify("Free Market").Ok);
        Assert.True(FmMapGate.Classify("Free Market Entrance").Ok);

        // Windows OCR often reads room 1 as l / I / |.
        var ocrL = FmMapGate.Classify("Free Market<l>");
        Assert.True(ocrL.Ok);
        Assert.Equal("Free Market<1>", ocrL.MatchedName);
        Assert.True(FmMapGate.Classify("Free Market<I>").Ok);
        Assert.Equal("Free Market<1>", FmMapGate.Classify("Free Market<I>").MatchedName);
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap("Free Market<l>"));
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap("Hidden Street | Free Market<l>"));
        Assert.Equal("Free Market<1>", ModeMapPicker.Resolve("", ["Hidden Street", "Free Market<l>"], "", "Free Market<l>"));

        var hunting = FmMapGate.Classify("Mushroom Garden");
        Assert.False(hunting.Ok);
        Assert.Equal("not an FM map", hunting.Reason);
        Assert.Equal("Mushroom Garden", hunting.Detected);

        using var maps = JsonDocument.Parse(File.ReadAllText(DataFiles.Require("fm-maps.json")));
        var named = false;
        foreach (var map in maps.RootElement.GetProperty("maps").EnumerateArray())
        {
            var name = map.GetProperty("name").GetString();
            Assert.False(string.IsNullOrWhiteSpace(name));
            var gate = FmMapGate.Classify(name);
            Assert.True(gate.Ok, name);
            Assert.Null(gate.ChannelFromName);
            named = true;
        }

        Assert.True(named);
    }

    [Fact]
    public void Scanner_follows_the_map_and_a_free_market_line_wins_over_a_held_hunt()
    {
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap("Free Market<1>"));
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap("FM 1"));
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap("FM Channel 1"));
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap("Free Market Entrance"));
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap("Henesys Free Market <1>"));
        Assert.Equal(ScannerMode.Training, ScannerModeChoice.ForMap("Mushroom Garden"));
        Assert.Equal(ScannerMode.Training, ScannerModeChoice.ForMap("unknown"));
        Assert.Equal(ScannerMode.Training, ScannerModeChoice.ForMap(""));
        Assert.Equal(ScannerMode.Training, ScannerModeChoice.ForMap(null));

        var mode = ModeMapPicker.Resolve("", ["Mushroom Garden"], "", "Mushroom Garden");
        Assert.Equal("Mushroom Garden", mode);
        Assert.Equal(ScannerMode.Training, ScannerModeChoice.ForMap(mode));

        mode = ModeMapPicker.Resolve(mode, ["Ludibrium", "Free Market<1>"], "Mushroom Garden", "Mushroom Garden");
        Assert.Equal("Free Market<1>", mode);
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap(mode));

        mode = ModeMapPicker.Resolve(mode, ["????"], "Mushroom Garden", "Mushroom Garden");
        Assert.Equal("Free Market<1>", mode);
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap(mode));

        mode = ModeMapPicker.Resolve(mode, ["Henesys", "Mushroom Garden"], "Mushroom Garden", "Mushroom Garden");
        Assert.Equal("Mushroom Garden", mode);
        Assert.Equal(ScannerMode.Training, ScannerModeChoice.ForMap(mode));

        // OCR junk must not kick FM mode into Training (live bug: "GoC").
        mode = ModeMapPicker.Resolve("Free Market<20>", ["GoC"], "Free Market<20>", "GoC");
        Assert.Equal("Free Market<20>", mode);
        Assert.Equal(ScannerMode.Fm, ScannerModeChoice.ForMap(mode));

        var hold = new MapHold();
        Assert.False(hold.Observe(new MapChoice("Free Market<21>", null, false)));
        Assert.True(hold.Observe(new MapChoice("Free Market<21>", null, false)));
        Assert.Equal("Free Market<21>", hold.Value);
        Assert.False(hold.Observe(new MapChoice("GoC", null, false)));
        Assert.False(hold.Observe(new MapChoice("GoC", null, false)));
        Assert.Equal("Free Market<21>", hold.Value);
    }

    [Fact]
    public void Scrollbar_skips_until_it_moves_and_an_owner_change_resets()
    {
        var frames = FmFixture.LoadFrames();
        var machine = new FmScanMachine();

        var skipped = machine.Step(frames[0]);
        Assert.Equal("skipped", skipped.Kind);
        Assert.Equal("not an FM map", skipped.Reason);
        Assert.Contains("Mushroom Garden — not an FM map", skipped.Message);
        Assert.Empty(skipped.Added);
        Assert.Null(machine.Shop);

        var opened = machine.Step(frames[1]);
        Assert.Equal("scanned", opened.Kind);
        Assert.Equal(3, opened.Added.Count);
        Assert.Equal("Sample Seller", machine.Shop!.Owner);
        Assert.Equal("Sample Scroll Shelf", machine.Shop.ShopName);
        Assert.Equal("Free Market<1>", machine.Shop.MapName);
        Assert.Equal(4, machine.Shop.Channel);

        var held = machine.Step(frames[2]);
        Assert.Equal("unchanged", held.Kind);
        Assert.Contains("Skipped a rescan", held.Message);
        Assert.Empty(held.Added);
        Assert.Equal(3, machine.Shop.Lines.Count);

        var moved = machine.Step(frames[3]);
        Assert.Equal("scanned", moved.Kind);
        Assert.False(moved.Reset);
        Assert.Equal(
            new[] { "Scroll for Earring for LUK 60%", "Cursed Maple Relic" },
            moved.Added.Select(row => row.Item).ToArray());
        Assert.Equal(5, machine.Shop.Lines.Count);
        Assert.Equal(0.25, machine.Shop.Scrollbar.Position);
        Assert.Equal(2, machine.Shop.Scrollbar.First);
        Assert.Equal(3, machine.Shop.Scrollbar.Visible);
        Assert.Equal(1, machine.Shop.Lines.Count(row => row.Item == "Orange Potion"));
        Assert.Equal(machine.Shop.Lines.Count, machine.Shop.Lines.Select(row => row.Identity).Distinct().Count());

        var again = machine.Step(frames[3]);
        Assert.Equal("unchanged", again.Kind);
        Assert.Equal(5, machine.Shop.Lines.Count);

        var replaced = machine.Step(new FmFrameInput
        {
            MapName = "Free Market<2>",
            Owner = "Other Seller",
            ShopName = "Sample Scroll Shelf",
            Channel = 4,
            Scrollbar = new FmScrollbar { Position = 0, First = 0, Visible = 1 },
            Rows =
            [
                new FmVisibleRow { Item = "Orange Potion", Quantity = 1, PriceEach = 40, Slot = 0, Row = 0 },
            ],
        });
        Assert.True(replaced.Reset);
        Assert.Contains("Reset the shop", replaced.Message);
        Assert.Equal("Other Seller", machine.Shop.Owner);
        Assert.Equal(new[] { "Orange Potion" }, machine.Shop.Lines.Select(row => row.Item).ToArray());
    }

    [Fact]
    public void Price_labels_follow_the_bundled_json()
    {
        // The comments name the figures in the files today. The assertions
        // read those files. If a file changes, the file wins.
        var ilbiFile = DiscordMeso("Ilbi Throwing-Stars");
        var ilbi = PriceCatalog.Lookup("Ilbi Throwing-Stars");
        Assert.Equal("sold", ilbiFile.Source);
        Assert.Equal("Discord market · sold", ilbi.Label);
        Assert.Equal(ilbiFile.Meso, ilbi.Meso);
        Assert.Equal(12_200_000, ilbiFile.Meso);

        var helmetFile = DiscordMeso("Scroll for Helmet for DEX 30%");
        var helmet = PriceCatalog.Lookup("Scroll for Helmet for DEX 30%");
        Assert.Equal("consensus", helmetFile.Source);
        Assert.Equal("Discord market · asks", helmet.Label);
        Assert.Equal(helmetFile.Meso, helmet.Meso);
        Assert.Equal(600_000, helmetFile.Meso);

        var earringFile = ClassicMeso("Scroll for Earring for LUK 60%");
        var earring = PriceCatalog.Lookup("Scroll for Earring for LUK 60%");
        Assert.Equal("Classic", earring.Label);
        Assert.Equal(earringFile, earring.Meso);
        Assert.Equal(89_900, earringFile);

        var potionFile = NpcPrice("Orange Potion");
        var potion = PriceCatalog.Lookup("Orange Potion");
        Assert.Equal("NPC", potion.Label);
        Assert.Equal(potionFile, potion.Meso);
        Assert.Equal(80, potionFile);

        var piece = PriceCatalog.Lookup("Piece of Time");
        Assert.Equal("NPC", piece.Label);
        Assert.Equal(2_000, piece.Meso);

        var stimulator = PriceCatalog.Lookup("Timeless Production Stimulator");
        Assert.Equal("No listed price", stimulator.Label);
        Assert.Null(stimulator.Meso);
        Assert.Equal("No listed price", PriceCatalog.GapText(1, stimulator));

        var puppet = PriceCatalog.Lookup("Puppet Robot SHISHIMARU");
        Assert.Equal("No listed price", puppet.Label);
        Assert.Null(puppet.Meso);

        var missing = PriceCatalog.Lookup("Cursed Maple Relic");
        Assert.Equal("Not in the database", missing.Label);
        Assert.Null(missing.Meso);
        Assert.Equal("Not in the database", PriceCatalog.GapText(123456, missing));

        var gap = PriceCatalog.GapText(10_000_000, ilbi);
        Assert.Contains("under the database", gap);
    }

    [Fact]
    public void Training_fixture_counts_each_loot_line_once()
    {
        var fixture = TrainingFixture.Load();
        var session = new TrainingSession();
        session.ApplySample(fixture, "Hunter");

        Assert.False(session.Ocr);
        Assert.True(session.Sample);
        Assert.Equal(120, session.Kills);
        Assert.Equal(15 * 60 * 1000, session.DurationMs);
        Assert.Equal(53_040, session.Exp);
        Assert.Equal(1_844_000, session.Damage);
        Assert.Equal(960, session.Attacks);
        Assert.NotEqual(session.Kills, session.Damage);

        var cap = Assert.Single(session.Drops, drop => drop.ItemName == "Orange Mushroom Cap");
        var potion = Assert.Single(session.Drops, drop => drop.ItemName == "Red Potion");
        Assert.Equal(10, cap.Quantity);
        Assert.Equal(2, potion.Quantity);
        Assert.Equal(1, cap.Notices);
        Assert.Equal(1, potion.Notices);

        var repeated = session.Ingest(fixture.Frames);
        Assert.Equal(2, repeated.Skipped);
        Assert.Empty(repeated.Drops);
        Assert.Equal(10, Assert.Single(session.Drops, drop => drop.ItemName == "Orange Mushroom Cap").Quantity);
        Assert.Equal(2, Assert.Single(session.Drops, drop => drop.ItemName == "Red Potion").Quantity);
        Assert.Equal(120, session.Kills);
    }

    private static (long Meso, string Source) DiscordMeso(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DataFiles.Require("osm-market-prices.json")));
        foreach (var row in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (row.GetProperty("name").GetString() == name)
            {
                return (row.GetProperty("meso").GetInt64(), row.GetProperty("source").GetString() ?? "");
            }
        }

        throw new InvalidOperationException($"{name} is not in osm-market-prices.json.");
    }

    private static long ClassicMeso(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DataFiles.Require("catalog.json")));
        foreach (var row in doc.RootElement.GetProperty("scrolls").EnumerateArray())
        {
            if (row.GetProperty("name").GetString() == name) return row.GetProperty("meso").GetInt64();
        }

        throw new InvalidOperationException($"{name} is not in catalog scrolls.");
    }

    private static long NpcPrice(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DataFiles.Require("catalog.json")));
        foreach (var row in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (row.GetProperty("name").GetString() == name) return row.GetProperty("price").GetInt64();
        }

        throw new InvalidOperationException($"{name} is not in catalog items.");
    }

    [Fact]
    public void Chrome_finder_locates_minimap_shop_and_scrollbar()
    {
        var width = 800;
        var height = 600;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 80, 140, 70);
        DrawPanel(pixels, width, 8, 40, 150, 170, varied: true, scrollbar: false, thumbAt: 0);
        DrawPanel(pixels, width, 180, 100, 420, 400, varied: false, scrollbar: true, thumbAt: 0.5);

        var chrome = UiChromeFinder.Find(pixels, width, height, width * 4);

        Assert.True(chrome.Minimap != null, chrome.Note);
        Assert.InRange(chrome.Minimap.Value.X, 4, 16);
        Assert.InRange(chrome.Minimap.Value.Y, 36, 48);
        Assert.NotNull(chrome.Shop);
        Assert.InRange(chrome.Shop.Value.X, 170, 190);
        Assert.NotNull(chrome.Scrollbar);
        Assert.NotNull(chrome.ScrollPosition);
        Assert.InRange(chrome.ScrollPosition.Value, 0.35, 0.7);
        Assert.NotNull(chrome.ShopRows);
        Assert.True(chrome.ShopRows.Value.Right <= chrome.Scrollbar.Value.X + 4);
        Assert.True(chrome.ShopRows.Value.Y >= chrome.Scrollbar.Value.Y - 2);
    }

    [Fact]
    public void Scrollbar_sits_on_the_list_and_not_the_pale_preview()
    {
        var width = 800;
        var height = 600;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 80, 140, 70);
        DrawPanel(pixels, width, 180, 100, 420, 400, varied: false, scrollbar: false, thumbAt: 0);
        PaintRect(pixels, width, 400, 130, 180, 360, 236, 226, 222);
        PaintRect(pixels, width, 200, 300, 174, 170, 216, 190, 189);
        PaintRect(pixels, width, 374, 300, 12, 170, 216, 190, 189);
        PaintRect(pixels, width, 374, 278, 12, 22, 80, 90, 120);
        PaintRect(pixels, width, 374, 470, 12, 22, 80, 90, 120);
        var trackHeight = 170;
        var thumbHeight = 36;
        var thumbTop = 300 + (int)((trackHeight - thumbHeight) * 0.15);
        PaintRect(pixels, width, 374, thumbTop, 12, thumbHeight, 129, 147, 167);

        var chrome = UiChromeFinder.Find(pixels, width, height, width * 4);

        Assert.True(chrome.Scrollbar != null, chrome.Note);
        Assert.InRange(chrome.Scrollbar.Value.X, 368, 390);
        Assert.InRange(chrome.Scrollbar.Value.Y, 260, 310);
        Assert.InRange(chrome.Scrollbar.Value.Bottom, 470, 510);
        Assert.NotNull(chrome.ScrollPosition);
        Assert.InRange(chrome.ScrollPosition.Value, 0.05, 0.30);
        Assert.NotNull(chrome.ShopRows);
        Assert.True(chrome.ShopRows.Value.Y > 250, chrome.Note);
        Assert.True(chrome.ShopRows.Value.Right <= chrome.Scrollbar.Value.X + 2);
        Assert.True(chrome.ShopRows.Value.Bottom <= chrome.Scrollbar.Value.Bottom + 2);
    }

    [Fact]
    public void Scrollbar_spans_the_arrow_buttons_when_the_track_color_breaks()
    {
        var width = 800;
        var height = 600;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 80, 140, 70);
        DrawPanel(pixels, width, 180, 100, 420, 400, varied: false, scrollbar: false, thumbAt: 0);
        PaintRect(pixels, width, 400, 130, 180, 360, 236, 226, 222);
        PaintRect(pixels, width, 200, 160, 174, 292, 216, 190, 189);
        PaintRect(pixels, width, 374, 160, 12, 22, 80, 90, 120);
        PaintRect(pixels, width, 374, 182, 12, 90, 216, 190, 189);
        PaintRect(pixels, width, 374, 272, 12, 40, 180, 70, 70);
        PaintRect(pixels, width, 374, 312, 12, 118, 216, 190, 189);
        PaintRect(pixels, width, 374, 430, 12, 22, 80, 90, 120);

        var chrome = UiChromeFinder.Find(pixels, width, height, width * 4);

        Assert.True(chrome.Scrollbar != null, chrome.Note);
        Assert.InRange(chrome.Scrollbar.Value.X, 368, 390);
        Assert.InRange(chrome.Scrollbar.Value.Y, 150, 175);
        Assert.InRange(chrome.Scrollbar.Value.Bottom, 440, 460);
        Assert.Null(chrome.ScrollPosition);
        Assert.NotNull(chrome.ShopRows);
        Assert.InRange(chrome.ShopRows.Value.Y, chrome.Scrollbar.Value.Y - 2, chrome.Scrollbar.Value.Y + 2);
        Assert.InRange(chrome.ShopRows.Value.Bottom, chrome.Scrollbar.Value.Bottom - 2, chrome.Scrollbar.Value.Bottom + 2);
        Assert.True(chrome.ShopRows.Value.Height > 250, chrome.Note);
    }

    [Fact]
    public void A_bright_wide_minimap_is_not_stored_as_the_shop()
    {
        var width = 800;
        var height = 600;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 80, 140, 70);
        DrawPanel(pixels, width, 8, 36, 340, 240, varied: true, scrollbar: false, thumbAt: 0, bright: true);
        DrawPanel(pixels, width, 450, 180, 260, 220, varied: false, scrollbar: true, thumbAt: 0.4);

        var chrome = UiChromeFinder.Find(pixels, width, height, width * 4);

        Assert.True(chrome.Minimap != null, chrome.Note);
        Assert.InRange(chrome.Minimap.Value.X, 4, 16);
        Assert.InRange(chrome.Minimap.Value.Width, 320, 360);
        Assert.NotNull(chrome.MapTitle);
        Assert.True(chrome.MapTitle.Value.Y < chrome.Minimap.Value.Y + 80);
        Assert.NotNull(chrome.Shop);
        Assert.True(chrome.Shop.Value.X > 400);
    }

    [Fact]
    public void Default_merchant_beats_the_blank_character_panel_beside_it()
    {
        // Live bug: default shop placement sits left of a larger empty chat
        // panel; largest-area pick selected the blank panel and read 0 rows.
        var width = 1200;
        var height = 800;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 80, 140, 70);
        DrawPanel(pixels, width, 12, 10, 200, 180, varied: true, scrollbar: false, thumbAt: 0);
        // Pale list panel with a right-edge scrollbar (not the mid-width test track).
        DrawPanel(pixels, width, 220, 200, 360, 420, varied: false, scrollbar: false, thumbAt: 0);
        for (var y = 230; y < 600; y++)
        {
            for (var x = 230; x < 560; x++)
            {
                var on = ((x / 10) + (y / 12)) % 2 == 0;
                Set(pixels, width, x, y, (byte)(on ? 200 : 230), (byte)(on ? 208 : 236), (byte)(on ? 220 : 244));
            }
        }

        for (var y = 240; y < 590; y++)
        {
            for (var x = 555; x < 565; x++)
            {
                Set(pixels, width, x, y, UiChromeFinder.TrackR, UiChromeFinder.TrackG, UiChromeFinder.TrackB);
            }
        }

        for (var y = 300; y < 360; y++)
        {
            for (var x = 555; x < 565; x++)
            {
                Set(pixels, width, x, y, UiChromeFinder.ThumbR, UiChromeFinder.ThumbG, UiChromeFinder.ThumbB);
            }
        }

        DrawPanel(pixels, width, 600, 220, 480, 400, varied: false, scrollbar: false, thumbAt: 0, bright: true);
        for (var y = 250; y < 580; y++)
        {
            for (var x = 620; x < 1050; x++)
            {
                Set(pixels, width, x, y, 248, 248, 252);
            }
        }

        var chrome = UiChromeFinder.Find(pixels, width, height, width * 4);

        Assert.True(chrome.Shop != null, chrome.Note);
        Assert.InRange(chrome.Shop.Value.X, 200, 280);
        Assert.True(chrome.Shop.Value.X + chrome.Shop.Value.Width < 600, chrome.Note);
        Assert.NotNull(chrome.Scrollbar);
    }

    [Fact]
    public void Minimap_includes_the_canvas_and_the_name_band_sits_on_the_title()
    {
        var width = 800;
        var height = 800;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 80, 140, 70);
        DrawPanel(pixels, width, 20, 30, 280, 340, varied: true, scrollbar: false, thumbAt: 0);
        for (var col = 0; col < 280; col++)
        {
            Set(pixels, width, 20 + col, 140, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
            Set(pixels, width, 20 + col, 141, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
        }

        var chrome = UiChromeFinder.Find(pixels, width, height, width * 4);

        Assert.True(chrome.Minimap != null, chrome.Note);
        Assert.InRange(chrome.Minimap.Value.Height, 300, 360);
        Assert.NotNull(chrome.MapTitle);
        Assert.InRange(chrome.MapTitle.Value.Bottom, 120, 142);
        Assert.True(chrome.MapTitle.Value.Y > chrome.Minimap.Value.Y + 20);
    }

    [Fact]
    public void Toy_factory_without_brackets_replaces_crossroad_of_time()
    {
        var read = MapTitle.Choose(["Toy Factory Process 1 Zone 1"]);
        Assert.NotNull(read);
        Assert.True(read.Value.FromCatalog);
        Assert.Equal("Toy Factory <Process 1> Zone 1", read.Value.Name);
        Assert.Equal(220020000, read.Value.MapId);

        var map = new MapHold();
        Assert.True(map.Observe(MapTitle.Choose(["Crossroad of Time"])));
        Assert.Equal(220040200, map.MapId);
        Assert.True(map.Observe(read));
        Assert.Equal("Toy Factory <Process 1> Zone 1", map.Value);
        Assert.Equal(220020000, map.MapId);
    }

    [Fact]
    public void Shop_lines_become_priced_rows()
    {
        var rows = ShopListingParser.ParseRows(
        [
            "Ilbi Throwing-Stars 1 10,000,000",
            "Scroll for Helmet for DEX 30% 750,000",
            "Orange Potion x50 1,200",
            "Buy",
            "Close",
        ]);

        Assert.Equal(
            new[] { "Ilbi Throwing-Stars", "Scroll for Helmet for DEX 30%", "Orange Potion" },
            rows.Select(row => row.Item).ToArray());
        Assert.Equal(10_000_000, rows[0].PriceEach);
        Assert.Equal(1, rows[0].Quantity);
        Assert.Equal(750_000, rows[1].PriceEach);
        Assert.Equal(50, rows[2].Quantity);
        Assert.Equal(1_200, rows[2].PriceEach);

        var split = ShopListingParser.ParseRows(["Kumasuma", "99,999 mesos", "Orange Potion", "50", "80"]);
        Assert.Equal("Kumasuma", split[0].Item);
        Assert.Equal(99_999, split[0].PriceEach);
        Assert.Equal("Orange Potion", split[1].Item);
        Assert.Equal(50, split[1].Quantity);
        Assert.Equal(80, split[1].PriceEach);

        var hired = ShopListingParser.ParseRows(
        [
            "LERVESTORE ( 332 602 ) Hired Merchant",
            "Tavar",
            "0",
            "1,299,999 mesos",
            "The Peacemaker",
            "0",
            "1,299,999 mesos",
            "Khanjar",
            "0",
            "14,999,999 mesos",
            "Reverse Lampion",
            "0",
            "777,777,777 mesos",
            "[Mastery Book] Battleship Torpe",
            "0",
            "4,999,999 mesos",
        ]);
        Assert.Equal(
            new[] { "Tavar", "The Peacemaker", "Khanjar", "Reverse Lampion", "[Mastery Book] Battleship Torpe" },
            hired.Select(row => row.Item).ToArray());
        Assert.Equal(1_299_999, hired[0].PriceEach);
        Assert.Equal(1, hired[0].Quantity);
        Assert.Equal(777_777_777, hired[3].PriceEach);

        var glued = ShopListingParser.ParseRows(
        [
            "Tavar",
            "0 1,299,999 mesos",
            "The Peacemaker",
            "0 1,299,999 mesos",
        ]);
        Assert.Equal(new[] { "Tavar", "The Peacemaker" }, glued.Select(row => row.Item).ToArray());
        Assert.Equal(1_299_999, glued[0].PriceEach);

        var books = ShopListingParser.ParseRows(
        [
            "332 602",
            "[Mastery Book] Power Stance 20",
            "5,555,555 mesos",
            "[Mastery Book] Meteor Shower 3",
            "161,616,161 mesos",
        ]);
        Assert.Equal(new[] { "[Mastery Book] Power Stance 20", "[Mastery Book] Meteor Shower 3" }, books.Select(row => row.Item).ToArray());
        Assert.Equal(5_555_555, books[0].PriceEach);
        Assert.Equal(161_616_161, books[1].PriceEach);
    }

    [Fact]
    public void Shop_icon_stack_digits_replace_default_quantity_1()
    {
        // Live Mama store crop: top row stack is 5, second row is 1. Sold /
        // washed rows may stay at 1 until the pale-digit reader lands.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "shop-rows-stack-qty.png");
        using var bitmap = new Bitmap(path);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var bgra = new byte[width * height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var rows = Enumerable.Range(0, 5)
            .Select(i => new FmVisibleRow { Item = "Row" + i, Quantity = 1, PriceEach = 1000 + i, Row = i })
            .ToList();
        ShopStackDigits.Apply(bgra, width, height, width * 4, rows);
        Assert.Equal(5, rows[0].Quantity);
        Assert.Equal(1, rows[1].Quantity);
    }

    [Fact]
    public void Shop_icon_stack_digits_read_scroll_qty_ground_truth()
    {
        // Mildy scrolls crop ground truth: 46 / 0 / 1 / 43 / 1.
        // Sold/empty row stays at the default 1 (Absorb leaves it).
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "shop-rows-scrolls-qty.png");
        using var bitmap = new Bitmap(path);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var bgra = new byte[width * height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var rows = Enumerable.Range(0, 5)
            .Select(i => new FmVisibleRow { Item = "Row" + i, Quantity = 1, PriceEach = 1000 + i, Row = i })
            .ToList();
        ShopStackDigits.Apply(bgra, width, height, width * 4, rows);
        Assert.Equal(46, rows[0].Quantity);
        Assert.Equal(1, rows[1].Quantity);
        Assert.Equal(1, rows[2].Quantity);
        Assert.Equal(43, rows[3].Quantity);
        Assert.Equal(1, rows[4].Quantity);
    }

    [Fact]
    public void Shop_icon_stack_digits_read_ore_qty_ground_truth()
    {
        // KaPl ores crop ground truth: 24 Aqua / 0 LUK (sold) / 100 Mithril /
        // 68 Mithril / 73 Steel. Sold tone forces quantity 0 when digits wash out.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "shop-rows-ores-qty.png");
        using var bitmap = new Bitmap(path);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var bgra = new byte[width * height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var rows = Enumerable.Range(0, 5)
            .Select(i => new FmVisibleRow { Item = "Row" + i, Quantity = 1, PriceEach = 1000 + i, Row = i })
            .ToList();
        ShopStackDigits.Apply(bgra, width, height, width * 4, rows);
        ShopRowTone.Mark(bgra, width, height, width * 4, rows);
        foreach (var row in rows)
            if (row.Sold) row.Quantity = 0;
        Assert.Equal(24, rows[0].Quantity);
        Assert.Equal(0, rows[1].Quantity);
        Assert.Equal(100, rows[2].Quantity);
        Assert.Equal(68, rows[3].Quantity);
        Assert.Equal(73, rows[4].Quantity);
    }

    [Fact]
    public void Absorb_counts_how_many_of_an_item_exist_instead_of_default_1()
    {
        var machine = new FmScanMachine();
        var frame = new FmFrameInput
        {
            MapName = "Free Market<2>",
            Owner = "Ore Shop",
            ShopName = "Ores",
            Channel = 1,
            Scrollbar = new FmScrollbar { Position = 0, First = 0, Visible = 3 },
            Rows =
            [
                new FmVisibleRow { Item = "Diamond Ore", Quantity = 1, PriceEach = 4_000, Slot = 0, Row = 0 },
                new FmVisibleRow { Item = "Diamond Ore", Quantity = 1, PriceEach = 4_000, Slot = 1, Row = 1 },
                new FmVisibleRow { Item = "Diamond Ore", Quantity = 1, PriceEach = 4_000, Slot = 2, Row = 2 },
                new FmVisibleRow { Item = "Orange Potion", Quantity = 50, PriceEach = 80, Slot = 3, Row = 3 },
            ],
        };

        var step = machine.Absorb(frame);
        Assert.Equal(2, step.Added.Count);
        Assert.Equal(2, machine.Shop!.Lines.Count);
        var ore = Assert.Single(machine.Shop.Lines, line => line.Item == "Diamond Ore");
        Assert.Equal(3, ore.Quantity);
        Assert.Equal(4_000, ore.PriceEach);
        var potion = Assert.Single(machine.Shop.Lines, line => line.Item == "Orange Potion");
        Assert.Equal(50, potion.Quantity);

        frame.Rows =
        [
            new FmVisibleRow { Item = "Diamond Ore", Quantity = 1, PriceEach = 4_000, Slot = 0, Row = 0 },
            new FmVisibleRow { Item = "Diamond Ore", Quantity = 1, PriceEach = 4_000, Slot = 1, Row = 1 },
            new FmVisibleRow { Item = "Diamond Ore", Quantity = 1, PriceEach = 4_000, Slot = 2, Row = 2 },
            new FmVisibleRow { Item = "Diamond Ore", Quantity = 1, PriceEach = 4_000, Slot = 3, Row = 3 },
            new FmVisibleRow { Item = "Orange Potion", Quantity = 50, PriceEach = 80, Slot = 4, Row = 4 },
        ];
        var again = machine.Absorb(frame);
        Assert.Empty(again.Added);
        Assert.Equal(4, Assert.Single(machine.Shop.Lines, line => line.Item == "Diamond Ore").Quantity);
        Assert.Equal(50, Assert.Single(machine.Shop.Lines, line => line.Item == "Orange Potion").Quantity);
    }

    [Fact]
    public void Every_priced_shop_row_stays_including_two_prices_for_one_name()
    {
        var rows = ShopListingParser.ParseRows(
        [
            "Gold Dragon Crown",
            "0",
            "59,999,999 mesos",
            "Maple Soul Spear",
            "0",
            "29,999,999 mesos",
            "Maple Magician shield",
            "0",
            "8,888,888 mesos",
            "Maple Magician shield 0",
            "6,555,555 mesos",
            "Yellow Gaia Cape",
            "0",
            "49,999,999 mesos",
        ]);
        Assert.Equal(
            new[] { "Gold Dragon Crown", "Maple Soul Spear", "Maple Magician shield", "Maple Magician shield", "Yellow Gaia Cape" },
            rows.Select(row => row.Item).ToArray());
        Assert.Equal(new long[] { 59_999_999, 29_999_999, 8_888_888, 6_555_555, 49_999_999 }, rows.Select(row => row.PriceEach).ToArray());

        var listed = ShopListingParser.WithCatalogNames(rows);
        Assert.Equal(5, listed.Count);
        Assert.Equal("Gold Dragon Crown", listed[0].Item);
        Assert.Equal("Maple Soul Spear", listed[1].Item);
        Assert.Equal("Maple Magician shield", listed[2].Item);
        Assert.Equal(8_888_888, listed[2].PriceEach);
        Assert.Equal("Maple Magician shield", listed[3].Item);
        Assert.Equal(6_555_555, listed[3].PriceEach);
        Assert.Equal("Yellow Gaia Cape", listed[4].Item);
        Assert.Equal(49_999_999, listed[4].PriceEach);
    }

    [Fact]
    public void A_coin_read_as_o_is_not_part_of_the_item_name()
    {
        var rows = ShopListingParser.ParseRows(
        [
            "[Mastery Book] Enrage 20 o",
            "16,666,666 mesos",
            "Timeless Production Stimulator",
            "o",
            "19,999,999 mesos",
            "Orange Potion",
            "50",
            "0 1,200 mesos",
            "Elixir 200",
            "9,999 mesos",
        ]);

        Assert.Equal("[Mastery Book] Enrage 20", rows[0].Item);
        Assert.Equal(1, rows[0].Quantity);
        Assert.Equal(16_666_666, rows[0].PriceEach);
        Assert.Equal("Timeless Production Stimulator", rows[1].Item);
        Assert.Equal(1, rows[1].Quantity);
        Assert.Equal("Orange Potion", rows[2].Item);
        Assert.Equal(50, rows[2].Quantity);
        Assert.Equal(1_200, rows[2].PriceEach);
        Assert.Equal("Elixir", rows[3].Item);
        Assert.Equal(200, rows[3].Quantity);
        Assert.Equal(9_999, rows[3].PriceEach);

        var named = ShopListingParser.WithCatalogNames(rows);
        Assert.Equal("[Mastery Book] Enrage 20", named[0].Item);
        Assert.Equal("Timeless Production Stimulator", named[1].Item);
        Assert.Equal("Orange Potion", named[2].Item);
        Assert.Equal("Elixir", named[3].Item);
    }

    [Fact]
    public void A_scrolled_row_that_runs_into_the_next_item_is_left_out()
    {
        var rows = ShopListingParser.ParseRows(
        [
            "[Mastery Book] Genesis 20",
            "esos",
            "Zakum Chair",
            "99,999,999 mesos",
            "[Mastery Book] Genesis 20 esos Zakum Chair",
            "44,499,999 mesos",
            "[Mastery Book] Hurricane",
            "999,999 mesos",
        ]);

        Assert.DoesNotContain(rows, row => row.Item.Contains("esos", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rows, row => row.Item.Contains("Zakum", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("[Mastery Book] Hurricane", rows.Single().Item);
        Assert.Equal(999_999, rows[0].PriceEach);
    }

    [Fact]
    public void A_covered_name_stays_out_of_the_shop_list()
    {
        var rows = ShopListingParser.ParseRows(
        [
            "Scroll for Bow for ATT",
            "3,333,333 mesos",
            "Yellow Gaia Cape",
            "4,888,888 mesos",
            "Fallen Leaf Earrings",
            "2,999,999 mesos",
            "Red Justice Cape",
            "999,999 mesos",
            "Blue Justice Cape",
            "999,999 mesos",
            "Red Justi Cape",
            "999,999 mesos",
            "Red Justice pe",
            "999,999 mesos",
            "Red Justice Ca",
            "999,999 mesos",
            "Red Ju ice Cape",
            "999,999 mesos",
            "0 4,888,888 mes Fallen Leaf Earrings",
            "2,999,999 mesos",
            "Fallen Leaf Earrings Red Justice Cape",
            "999,999 mesos",
            "0 2,",
            "999,999 mesos",
        ]);

        var listed = ShopListingParser.WithCatalogNames(rows, out var hidden);
        Assert.Equal(
            new[]
            {
                "Scroll for Bow for ATT",
                "Yellow Gaia Cape",
                "Fallen Leaf Earrings",
                "Red Justice Cape",
                "Blue Justice Cape",
                "Red Justice Cape",
                "Red Justice Cape",
                "Red Justice Cape",
                "Red Justice Cape",
            },
            listed.Select(row => row.Item).ToArray());
        Assert.DoesNotContain(hidden, row => row.Item is "Red Justi Cape" or "Red Justice pe" or "Red Justice Ca" or "Red Ju ice Cape");
        Assert.Contains(hidden, row => row.Item.Contains("Fallen Leaf Earrings Red", StringComparison.Ordinal));
        Assert.Contains(hidden, row => row.Item.Contains("mes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hidden, row => row.Item == "0 2,");

        listed[3].Sold = true;
        ShopListingParser.HoldSalesWhileCovered(listed, hidden);
        Assert.False(listed[3].Sold);
        ShopListingParser.HoldSalesWhileCovered(listed, []);
        listed[3].Sold = true;
        Assert.True(listed[3].Sold);
    }

    [Fact]
    public void A_cursor_on_a_row_is_not_a_sale()
    {
        const int width = 80;
        const int height = 60;
        var pixels = new byte[width * height * 4];
        Paint(pixels, width, 0, 20, 206, 180, 179);
        Paint(pixels, width, 20, 40, 230, 214, 210);
        Paint(pixels, width, 40, 60, 206, 180, 179);
        for (var y = 22; y < 38; y++)
        {
            for (var x = 16; x < 32; x++)
            {
                var dark = x < 24 && y < 32;
                Set(pixels, width, x, y, (byte)(dark ? 12 : 250), (byte)(dark ? 12 : 250), (byte)(dark ? 12 : 250));
            }
        }

        var rows = new List<FmVisibleRow>
        {
            new() { Item = "One", Quantity = 1, PriceEach = 1 },
            new() { Item = "Two", Quantity = 1, PriceEach = 2 },
            new() { Item = "Three", Quantity = 1, PriceEach = 3 },
        };
        ShopRowTone.Mark(pixels, width, height, width * 4, rows);
        Assert.True(rows[1].Covered);
        Assert.False(rows[1].Sold);
        Assert.False(rows[0].Sold);
        Assert.False(rows[2].Sold);
    }

    [Fact]
    public void Shop_rows_match_the_database_and_stay_when_the_list_scrolls()
    {
        Assert.True(PriceCatalog.TryMatch("[Mastery Book] Power Stance 20", out var stance));
        Assert.Equal("[Mastery Book] Power Stance 20", stance);
        var stancePrice = PriceCatalog.Lookup("[Mastery Book] Power Stance 20");
        Assert.Equal("NPC", stancePrice.Label);
        Assert.Equal(1, stancePrice.Meso);
        Assert.Equal("NPC", PriceCatalog.Lookup("[Mastery Book] Blizzard").Label);
        Assert.Equal("NPC", PriceCatalog.Lookup("Topaz").Label);
        Assert.Equal(1000, PriceCatalog.Lookup("Topaz").Meso);
        Assert.Equal(150, PriceCatalog.Lookup("Topaz Ore").Meso);
        Assert.True(PriceCatalog.TryMatch("Miniature Pianus", out var pianus));
        Assert.Equal("Miniature Pianus", pianus);
        Assert.False(PriceCatalog.TryMatch("332 602", out _));
        Assert.False(PriceCatalog.TryMatch("Raccoon Mask", out _));

        var machine = new FmScanMachine();
        var first = machine.Absorb(ShopFrame("xiaao", "Shelf", "[Mastery Book] Power Stance", 5_555_555, "Minature Pianus", 999_999_999));
        Assert.Equal(2, first.Added.Count);
        Assert.Equal(2, machine.Shop!.Lines.Count);

        var scrolled = machine.Absorb(ShopFrame("xiaao", "Shelf", "[Mastery Book] Hypnotize", 4_444_444));
        Assert.Single(scrolled.Added);
        Assert.False(scrolled.Reset);
        Assert.Equal(
            new[] { "[Mastery Book] Power Stance", "Minature Pianus", "[Mastery Book] Hypnotize" },
            machine.Shop.Lines.Select(row => row.Item).ToArray());

        var again = machine.Absorb(ShopFrame("xiaao", "Shelf", "[Mastery Book] Power Stance", 5_555_555));
        Assert.Empty(again.Added);
        Assert.Equal(3, machine.Shop.Lines.Count);

        var other = machine.Absorb(ShopFrame("other", "Shelf", "Minature Pianus", 100));
        Assert.True(other.Reset);
        Assert.Equal(new[] { "Minature Pianus" }, machine.Shop.Lines.Select(row => row.Item).ToArray());
    }

    [Fact]
    public void A_listing_already_on_the_list_is_marked_sold()
    {
        var machine = new FmScanMachine();
        var frame = ShopFrame("Kate", "Books", "[Mastery Book] Power Stance", 5_555_555);
        machine.Absorb(frame);
        Assert.False(machine.Shop!.Lines[0].Sold);

        frame.Rows[0].Sold = true;
        var again = machine.Absorb(frame);
        Assert.Empty(again.Added);
        Assert.True(machine.Shop.Lines[0].Sold);
    }

    [Fact]
    public void Listing_signature_includes_owner_so_two_shops_with_the_same_items_differ()
    {
        var first = ShopFrame("Alice", "Potions", "Orange Potion", 80);
        var second = ShopFrame("Bob", "Potions", "Orange Potion", 80);
        var sameOwnerAgain = ShopFrame("Alice", "Potions", "Orange Potion", 80);

        Assert.NotEqual(FmListingSignature.From(first), FmListingSignature.From(second));
        Assert.Equal(FmListingSignature.From(first), FmListingSignature.From(sameOwnerAgain));
    }

    [Fact]
    public void Absorb_after_a_reset_fills_the_shop_list_again_for_the_FM_table()
    {
        // Companion used to skip Absorb when only the item rows matched a prior
        // signature. After the shop machine is cleared, the next frame must put
        // every priced row back so LinesGrid is not left empty.
        var machine = new FmScanMachine();
        machine.Absorb(ShopFrame("Alice", "Potions", "Orange Potion", 80, "Red Potion", 100));
        Assert.Equal(2, machine.Shop!.Lines.Count);

        machine.Reset();
        Assert.Null(machine.Shop);

        var again = machine.Absorb(ShopFrame("Alice", "Potions", "Orange Potion", 80, "Red Potion", 100));
        Assert.Equal(2, again.Added.Count);
        Assert.Equal(
            new[] { "Orange Potion", "Red Potion" },
            machine.Shop!.Lines.Select(line => line.Item).ToArray());
    }

    [Fact]
    public void Absorb_keeps_listing_every_priced_row_when_the_same_frame_repeats()
    {
        var machine = new FmScanMachine();
        var frame = ShopFrame("Kate", "Books", "[Mastery Book] Power Stance", 5_555_555, "Topaz", 1000);
        var first = machine.Absorb(frame);
        Assert.Equal(2, first.Added.Count);
        Assert.Equal(2, machine.Shop!.Lines.Count);

        var repeat = machine.Absorb(frame);
        Assert.Empty(repeat.Added);
        Assert.Equal(2, machine.Shop.Lines.Count);
        Assert.Equal(
            new[] { "[Mastery Book] Power Stance", "Topaz" },
            machine.Shop.Lines.Select(line => line.Item).ToArray());
    }

    [Fact]
    public void A_pale_row_is_sold_and_a_missing_row_is_not()
    {
        var sales = new ListingSales();
        var sold = sales.Observe("Kate", "Books", "Free Market<2>",
        [
            Offer("One", 1, false),
            Offer("Two", 2, true),
            Offer("Three", 3, false),
        ]);
        var sale = Assert.Single(sold);
        Assert.Equal("Two", sale.Item);
        Assert.Equal(2, sale.PriceEach);
        Assert.Equal("Kate", sale.Owner);

        var again = sales.Observe("Kate", "Books", "Free Market<2>",
        [
            Offer("One", 1, false),
            Offer("Two", 2, true),
            Offer("Three", 3, false),
        ]);
        Assert.Empty(again);

        var missing = sales.Observe("Kate", "Books", "Free Market<2>",
        [
            Offer("One", 1, false),
            Offer("Three", 3, false),
        ]);
        Assert.Empty(missing);

        var otherShop = sales.Observe("Other", "Books", "Free Market<2>",
        [
            Offer("Two", 2, true),
        ]);
        Assert.Equal("Two", Assert.Single(otherShop).Item);
    }

    [Fact]
    public void A_paler_shop_row_is_the_sold_one()
    {
        const int width = 80;
        const int height = 60;
        var pixels = new byte[width * height * 4];
        Paint(pixels, width, 0, 20, 206, 180, 179);
        Paint(pixels, width, 20, 40, 230, 214, 210);
        Paint(pixels, width, 40, 60, 206, 180, 179);
        var rows = new List<FmVisibleRow>
        {
            new() { Item = "One", Quantity = 1, PriceEach = 1 },
            new() { Item = "Two", Quantity = 1, PriceEach = 2 },
            new() { Item = "Three", Quantity = 1, PriceEach = 3 },
        };
        ShopRowTone.Mark(pixels, width, height, width * 4, rows);
        Assert.False(rows[0].Sold);
        Assert.True(rows[1].Sold);
        Assert.False(rows[2].Sold);
    }

    [Fact]
    public void Battle_window_follows_the_orange_start_button()
    {
        var placed = PaintBattle(640, 420, 280, 36, 16, cancel: false);
        var window = BattleWindowFinder.Find(placed.Pixels, placed.Width, placed.Height, placed.Width * 4);
        AssertOnPanel(window, placed);
        Assert.Equal(BattleButton.Start, BattleWindowFinder.FindHit(placed.Pixels, placed.Width, placed.Height, placed.Width * 4)!.Value.Button);
    }

    [Fact]
    public void Battle_window_ignores_a_corner_blob_with_no_title()
    {
        const int width = 640;
        const int height = 420;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 30, 90, 200);
        PaintRect(pixels, width, 520, 360, 70, 16, 170, 204, 34);
        Assert.Null(BattleWindowFinder.Find(pixels, width, height, width * 4));
    }

    [Fact]
    public void Battle_window_finds_a_gray_title_bar()
    {
        var placed = PaintBattle(640, 420, 280, 36, 16, cancel: true);
        PaintRect(placed.Pixels, placed.Width, placed.PanelX, placed.PanelY, placed.PanelW, 16, 166, 166, 166);
        var inkTop = placed.PanelY + 3;
        for (var col = placed.PanelX + 6; col < placed.PanelX + placed.PanelW - 6; col += 7)
        {
            PaintRect(placed.Pixels, placed.Width, col, inkTop, 2, 8, 20, 20, 20);
        }

        var window = BattleWindowFinder.Find(placed.Pixels, placed.Width, placed.Height, placed.Width * 4);
        AssertOnPanel(window, placed);
    }

    [Fact]
    public void Battle_window_finds_a_panel_on_the_left()
    {
        var placed = PaintBattle(640, 420, 24, 40, 16, cancel: true);
        var window = BattleWindowFinder.Find(placed.Pixels, placed.Width, placed.Height, placed.Width * 4);
        AssertOnPanel(window, placed);
    }

    [Fact]
    public void Battle_read_of_trade_chat_is_not_the_panel()
    {
        Assert.False(BattleWindowFinder.LooksLikePanel(["TRADE"]));
        Assert.False(BattleWindowFinder.LooksLikePanel(["You ha", "TRADE"]));
        Assert.True(BattleWindowFinder.LooksLikePanel(["Dmg: 2.18M (441/s)", "Mob: 480"]));
        Assert.True(BattleWindowFinder.LooksLikePanel(["Elapsed", "01:30"]));
    }

    [Fact]
    public void Loot_and_name_follow_a_short_status_bar()
    {
        const int width = 800;
        const int height = 400;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var scene = y < 368;
                var r = (byte)(scene ? (x % 2 == 0 ? 30 : 190) : 150);
                var g = (byte)(scene ? (x % 2 == 0 ? 90 : 40) : 140);
                var b = (byte)(scene ? (x % 2 == 0 ? 40 : 30) : 130);
                Set(pixels, width, x, y, r, g, b);
            }
        }

        var bar = BottomBarFinder.Measure(pixels, width, height, width * 4);
        Assert.True(bar.Measured);
        Assert.InRange(bar.Top, 360, 376);
        var name = CharacterNameRegion.Place(width, height, bar);
        var loot = LootRegion.Place(width, height, bar);
        Assert.True(name.Y >= bar.Top && name.Bottom <= bar.Bottom);
        Assert.True(loot.Bottom <= bar.Top);
        Assert.True(loot.Y > height / 2);
    }

    [Fact]
    public void Battle_window_follows_the_green_cancel_button()
    {
        var placed = PaintBattle(640, 420, 280, 36, 16, cancel: true);
        var window = BattleWindowFinder.Find(placed.Pixels, placed.Width, placed.Height, placed.Width * 4);
        AssertOnPanel(window, placed);

        var moved = PaintBattle(640, 420, 300, 150, 16, cancel: true);
        var shifted = BattleWindowFinder.Find(moved.Pixels, moved.Width, moved.Height, moved.Width * 4);
        AssertOnPanel(shifted, moved);
        Assert.True(shifted!.Value.Y > window!.Value.Y + 80);
    }

    [Fact]
    public void Battle_window_tracks_a_resized_panel()
    {
        var small = PaintBattle(640, 420, 280, 36, 16, cancel: true);
        var large = PaintBattle(1280, 840, 560, 72, 32, cancel: false);
        var smallWindow = BattleWindowFinder.Find(small.Pixels, small.Width, small.Height, small.Width * 4);
        var largeWindow = BattleWindowFinder.Find(large.Pixels, large.Width, large.Height, large.Width * 4);
        AssertOnPanel(smallWindow, small);
        AssertOnPanel(largeWindow, large);
        Assert.True(largeWindow!.Value.Width > smallWindow!.Value.Width * 3 / 2);
        Assert.True(largeWindow.Value.Height > smallWindow.Value.Height * 3 / 2);
    }

    [Fact]
    public void Battle_window_ignores_foliage_and_a_left_side_blob()
    {
        const int width = 640;
        const int height = 420;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 30, 90, 200);
        PaintRect(pixels, width, 8, 40, 90, 36, 80, 170, 70);
        PaintRect(pixels, width, 20, 120, 70, 24, 230, 140, 40);
        Assert.Null(BattleWindowFinder.Find(pixels, width, height, width * 4));
    }

    [Fact]
    public void Battle_guide_keeps_the_last_window_when_the_button_blinks()
    {
        var placed = PaintBattle(640, 480, 280, 40, 16, cancel: true);
        var track = new BattleWindowFinder.Track();
        var first = track.Observe(placed.Pixels, placed.Width, placed.Height, placed.Width * 4);
        AssertOnPanel(first, placed);

        var sky = new byte[placed.Pixels.Length];
        Fill(sky, 30, 90, 200);
        for (var i = 0; i < BattleWindowFinder.Track.HoldFrames; i++)
        {
            var held = track.Observe(sky, placed.Width, placed.Height, placed.Width * 4);
            Assert.Equal(first, held);
        }

        Assert.Null(track.Observe(sky, placed.Width, placed.Height, placed.Width * 4));

        var home = new BattleWindowFinder.Track();
        Assert.NotNull(home.Observe(placed.Pixels, placed.Width, placed.Height, placed.Width * 4));
        var elsewhere = PaintBattle(640, 480, 280, 260, 16, cancel: false);
        var stayed = home.Observe(elsewhere.Pixels, elsewhere.Width, elsewhere.Height, elsewhere.Width * 4);
        Assert.Equal(first, stayed);
        var followed = home.Observe(elsewhere.Pixels, elsewhere.Width, elsewhere.Height, elsewhere.Width * 4);
        AssertOnPanel(followed, elsewhere);
    }

    [Fact]
    public void Loot_notices_are_the_bottom_right_band()
    {
        var band = LootRegion.Bounds(1024, 588);
        Assert.True(band.X > 1024 / 2);
        Assert.True(band.Y > 588 / 2);
        Assert.True(band.Y + band.Height < 588 * 0.9);
        Assert.True(band.X + band.Width > 900);
        Assert.True(band.Y > 588 * 0.65);
    }

    [Fact]
    public void Character_name_plate_stays_out_of_the_loot_band()
    {
        var name = CharacterNameRegion.Bounds(1024, 588);
        var loot = LootRegion.Bounds(1024, 588);
        Assert.True(name.Right <= loot.X);
        Assert.True(name.Y >= loot.Bottom);
        Assert.True(name.X < 400);
        Assert.InRange(name.Y, 520, 588);
        Assert.True(name.X <= 120 && name.X + name.Width >= 120);
        Assert.True(name.Y <= 572 && name.Y + name.Height >= 572);
    }

    [Fact]
    public void Battle_button_reads_start_or_cancel_and_one_miss_holds()
    {
        var running = PaintBattle(640, 420, 280, 36, 16, cancel: true);
        var hit = BattleWindowFinder.FindHit(running.Pixels, running.Width, running.Height, running.Width * 4);
        Assert.Equal(BattleButton.Cancel, hit!.Value.Button);

        var idle = PaintBattle(640, 420, 280, 36, 16, cancel: false);
        var start = BattleWindowFinder.FindHit(idle.Pixels, idle.Width, idle.Height, idle.Width * 4);
        Assert.Equal(BattleButton.Start, start!.Value.Button);

        var track = new BattleWindowFinder.Track();
        var seen = track.ObserveSight(running.Pixels, running.Width, running.Height, running.Width * 4);
        Assert.False(seen.Held);
        Assert.False(seen.Lost);
        Assert.Equal(BattleButton.Cancel, seen.Button);

        var sky = new byte[running.Pixels.Length];
        Fill(sky, 30, 90, 200);
        var missed = track.ObserveSight(sky, running.Width, running.Height, running.Width * 4);
        Assert.True(missed.Held);
        Assert.False(missed.Lost);
        Assert.Equal(seen.Window, missed.Window);
        Assert.Equal(BattleButton.Cancel, missed.Button);
    }

    [Fact]
    public void Live_battle_window_reads_mob_damage_exp_and_elapsed()
    {
        var clean = BattleText.Parse("Battle Analysis\nDmg: 900 (20/s)\nMob: 4 (1/m)\nExp: 120 (30/m)\nMeso: 50 (12/m)\nElapsed: 01:30\n");
        Assert.Equal(4, clean.Kills);
        Assert.Equal(120, clean.Exp);
        Assert.Equal(900, clean.Damage);
        Assert.Equal(50, clean.Meso);
        Assert.Equal(90_000, clean.DurationMs);

        var columns = BattleText.Parse("Battle Analysis\nDmg Mob Exp Meso\n8,234 (40/s) 51 (160/m) 105,000 (2,000/m) 3,434\nElapsed\n01:30\n");
        Assert.Equal(51, columns.Kills);
        Assert.Equal(8_234, columns.Damage);
        Assert.Equal(105_000, columns.Exp);
        Assert.Equal(3_434, columns.Meso);
        Assert.NotEqual(columns.Kills, columns.Damage);

        var messy = BattleText.Parse("Battle Analysis\neso:\napsed.\n• 00:00\nO (O/m)\nTRRT");
        Assert.Equal(0, messy.DurationMs);
        Assert.Null(messy.Kills);

        var stacked = BattleText.Parse("Battle Analysis\nDmg Mob Exp Meso\n8,234 (40/s)\n51 (160/m)\n105,000 (2,000/m)\n3,434\nElapsed\n01:30\n");
        Assert.Equal(51, stacked.Kills);
        Assert.Equal(8_234, stacked.Damage);
        Assert.Equal(105_000, stacked.Exp);
        Assert.Equal(3_434, stacked.Meso);
        Assert.Equal(90_000, stacked.DurationMs);

        var wrapped = BattleText.Parse("Battle Analysis\nDmg\nMob Exp Meso\n8,234 (40/s) 51 (160/m)\n105,000 (2,000/m) 3,434\nElapsed\n01:30\n");
        Assert.Equal(51, wrapped.Kills);
        Assert.Equal(8_234, wrapped.Damage);
        Assert.Equal(105_000, wrapped.Exp);
        Assert.Equal(3_434, wrapped.Meso);

        var damageOnly = BattleText.Parse("Battle Analysis\nDmg Mob Exp Meso\n8,234 (40/s)\nElapsed\n01:30\n");
        Assert.Null(damageOnly.Kills);
        Assert.Equal(8_234, damageOnly.Damage);

        var droppedLabel = BattleText.Parse("Battle Analysis\nMob Exp Meso\n8,234 (40/s) 51 (160/m) 105,000 (2,000/m) 3,434\nElapsed\n01:30\n");
        Assert.Null(droppedLabel.Kills);
        Assert.Null(droppedLabel.Damage);
        Assert.Null(droppedLabel.Exp);
        Assert.Null(droppedLabel.Meso);
    }

    [Fact]
    public void Royal_cactus_desert_mob_cell_replaces_a_damage_total_stored_as_kills()
    {
        const string sameLine =
            "Battle Analysis\nDmg: 266,090 (2,931/s)\nMob: 82 (60/m)\nExp: 7,544 (4,987/m)\nMeso: 7,426 (4,909/m)\nElapsed: 01:30\n";
        var lined = BattleText.Parse(sameLine);
        Assert.Equal(82, lined.Kills);
        Assert.Equal(266_090, lined.Damage);
        Assert.Equal(7_544, lined.Exp);
        Assert.Equal(7_426, lined.Meso);
        Assert.Equal(90_000, lined.DurationMs);

        const string split =
            "Battle Ana lysis\nDmg:\nMob:\nExp:\nMeso:\nElapsed\n266,090 (2,931/s)\n82 (60/m)\n7,544 (4,987/m)\n7,426 (4,909/m)\n: 01:30\n";
        var columns = BattleText.Parse(split);
        Assert.Equal(82, columns.Kills);
        Assert.Equal(266_090, columns.Damage);
        Assert.Equal(7_544, columns.Exp);
        Assert.Equal(7_426, columns.Meso);
        Assert.Equal(90_000, columns.DurationMs);

        var watch = new TrainingWatch();
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Idle,
            MapName = "Royal Cactus Desert",
            MapId = 260010201,
            CharacterName = "ExtraSin",
            AtMs = 1_000,
        });
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Royal Cactus Desert",
            MapId = 260010201,
            CharacterName = "ExtraSin",
            AtMs = 2_000,
            Battle = new BattleSnapshot
            {
                Kills = 262_999,
                Damage = 266_090,
                Exp = 7_544,
                Meso = 7_426,
                DurationMs = 90_000,
                Text = "damage column",
            },
        });
        Assert.Equal(262_999, watch.Shown!.Kills);

        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Royal Cactus Desert",
            MapId = 260010201,
            AtMs = 3_000,
            Battle = lined,
        });
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal(266_090, watch.Shown.Damage);

        var dipped = BattleText.Parse(
            "Battle Analysis\nDmg: 270,000 (3,000/s)\nMob: 80 (60/m)\nExp: 7,600 (4,900/m)\nMeso: 7,500 (4,900/m)\nElapsed: 01:32\n");
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Royal Cactus Desert",
            MapId = 260010201,
            AtMs = 4_000,
            Battle = dipped,
        });
        Assert.Equal(0, watch.Shown!.Kills);

        var more = BattleText.Parse(
            "Battle Analysis\nDmg: 280,000 (3,000/s)\nMob: 90 (60/m)\nExp: 8,000 (5,000/m)\nMeso: 8,000 (5,000/m)\nElapsed: 01:40\n");
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Royal Cactus Desert",
            MapId = 260010201,
            AtMs = 5_000,
            Battle = more,
        });
        Assert.Equal(8, watch.Shown!.Kills);
    }

    [Fact]
    public void Sky_forest_ocr_binds_the_mob_cell_and_replaces_a_stuck_exp_total()
    {
        var stuck = BattleText.Parse("Battle Analysis | 2.00M (1,016/s) | Dmg: | 420 (12/m) | Mob: | Exp: | 81,202 | Meso: | 39,3 | Elapsed. | • 32:48");
        Assert.Equal(420, stuck.Kills);
        Assert.True(stuck.KillsFromMob);
        Assert.Equal(2_000_000, stuck.Damage);
        Assert.Equal(81_202, stuck.Exp);
        Assert.Equal(1_968_000, stuck.DurationMs);

        var shifted = BattleText.Parse("1.86M (984/s) | Dmg: | 373 (111m) | Mob: | Exp: | Meso: | 74,438 (2,365/m) | 32,835 (1,043/m)");
        Assert.Equal(373, shifted.Kills);
        Assert.True(shifted.KillsFromMob);
        Assert.Equal(1_860_000, shifted.Damage);
        Assert.Equal(74_438, shifted.Exp);
        Assert.Equal(32_835, shifted.Meso);
        Assert.NotEqual(74_438, shifted.Kills);

        var splitMillion = BattleText.Parse("Battle Analysis | Dmg: | Mob: | Exp: | Meso: | Elapsed;3 | 1.86M (985/s) | 373 (11/m) | 438 | CHHCEL");
        Assert.Equal(373, splitMillion.Kills);
        Assert.Equal(1_860_000, splitMillion.Damage);
        Assert.NotEqual(373, splitMillion.Meso);
        Assert.NotEqual(1, splitMillion.Kills);
        Assert.NotEqual(86, splitMillion.Damage);

        var sameLine = BattleText.Parse("Mob: 82");
        Assert.Equal(82, sameLine.Kills);
        Assert.True(sameLine.KillsFromMob);

        var watch = new TrainingWatch();
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Idle,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 1_000,
        });
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 2_000,
            Battle = new BattleSnapshot
            {
                Kills = 81_201,
                Damage = 2_000_000,
                Exp = 81_202,
                DurationMs = 1_968_000,
                Text = "exp cell",
            },
        });
        Assert.Equal(81_201, watch.Shown!.Kills);

        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 3_000,
            Battle = stuck,
        });
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal("corrected", watch.BattleKillUse);

        var dip = BattleText.Parse("Dmg: 2.01M (500/s)\nMob: 418 (12/m)\nExp: 82,000 (1,200/m)\nMeso: 40,000 (600/m)\n");
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 4_000,
            Battle = dip,
        });
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal("ignored", watch.BattleKillUse);

        var more = BattleText.Parse("Dmg: 2.05M (510/s)\nMob: 430 (12/m)\nExp: 84,000 (1,200/m)\nMeso: 41,000 (600/m)\n");
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 5_000,
            Battle = more,
        });
        Assert.Equal(10, watch.Shown!.Kills);
    }

    [Fact]
    public void A_millions_figure_is_damage_and_a_mob_reading_replaces_it()
    {
        var mangled = BattleText.Parse("Battle Analysis | Dmg: | Mob: | Exp: | Meso. | '0 | 1.191M (5 Is) | 81,486 (1,33 | (64 m) | CHHCEL");
        Assert.Null(mangled.Kills);
        Assert.NotEqual(1_191_000, mangled.Kills);
        Assert.Equal(1_191_000, mangled.Damage);

        var at420 = BattleText.Parse("Battle Analysis | Dmg: | Mob: | Exp: | apse . | 2.00M (5 Is) | 420 ( /mk | 39,500 (64 | Meso• | CHHCEL");
        Assert.Equal(420, at420.Kills);
        Assert.True(at420.KillsFromMob);
        Assert.Equal(2_000_000, at420.Damage);

        var at425 = BattleText.Parse("Battle Analysis | 2.01M (5 Is) | Dmg: | 425 ( 1m) | Mob: | 81,918 (1,33 1m)? | Exp: | 39,702 (64 /hl)");
        Assert.Equal(425, at425.Kills);
        Assert.True(at425.KillsFromMob);
        Assert.Equal(2_010_000, at425.Damage);

        var watch = new TrainingWatch();
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Idle,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 1_000,
        });
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 2_000,
            Battle = new BattleSnapshot
            {
                Kills = 1_190_958,
                KillsFromMob = true,
                Damage = 1_191_000,
                Exp = 81_486,
                Text = "mangled millions",
            },
        });
        Assert.Equal(1_190_958, watch.Shown!.Kills);

        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 3_000,
            Battle = BattleText.Parse("Dmg: 2.01M (548/s)\nMob: 425 (5/m)\nExp: 81,918 (1,330/m)\nMeso: 39,702 (640/m)\n"),
        });
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal("corrected", watch.BattleKillUse);

        var dip = BattleText.Parse("Dmg: 2.00M (540/s)\nMob: 418 (5/m)\nExp: 81,000 (1,300/m)\nMeso: 39,000 (620/m)\n");
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 4_000,
            Battle = dip,
        });
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal("ignored", watch.BattleKillUse);
    }

    [Fact]
    public void A_bad_frame_is_ignored_and_a_good_mob_cell_replaces_a_stuck_total()
    {
        var shifted = BattleText.Parse("Mob: 2.16M (440/s) | Exp: 475 (5/m)");
        Assert.False(shifted.FrameGood);
        Assert.NotEqual(475, shifted.Kills);
        Assert.NotEqual(2_160_000, shifted.Kills);

        var good = BattleText.Parse("Dmg: 2.21M (447/s)\nMob: 491 (5/m)\nExp: 91,452 (1,110/m)\nMeso: 48,345 (587/m)\n");
        Assert.True(good.FrameGood);
        Assert.Equal(491, good.Kills);
        Assert.Equal(2_210_000, good.Damage);
        Assert.Equal(91_452, good.Exp);

        var watch = new TrainingWatch();
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Idle,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 1_000,
        });
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 2_000,
            Battle = new BattleSnapshot
            {
                Kills = 1_858,
                KillsFromMob = true,
                Damage = 2_160_000,
                Text = "stuck shown",
            },
        });
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 2_500,
            Battle = shifted,
        });
        Assert.Equal(1_858, watch.Shown!.Kills);
        Assert.Equal("rejected", watch.BattleKillUse);

        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 3_000,
            Battle = good,
        });
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal("corrected", watch.BattleKillUse);

        var dip = BattleText.Parse("Dmg: 2.20M (446/s)\nMob: 488 (5/m)\nExp: 91,000 (1,100/m)\nMeso: 48,000 (580/m)\n");
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 4_000,
            Battle = dip,
        });
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal("ignored", watch.BattleKillUse);

        var raw = new TrainingWatch();
        raw.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Idle,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 1_000,
        });
        raw.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 2_000,
            Battle = new BattleSnapshot
            {
                Kills = 87_392,
                KillsFromMob = true,
                Damage = 2_120_000,
                Text = "stuck exp total",
            },
        });
        raw.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 3_000,
            Battle = good,
        });
        Assert.Equal(0, raw.Shown!.Kills);
    }

    [Fact]
    public void A_grouped_panel_read_fills_the_session_counters()
    {
        const string panel =
            "Battle Analysis\nDmg:\nMob:\nExp:\nMeso:\n76,211 (834/s)\n28 (181m)\n4,042 (2,657/m)\n3,645 (2,396/m)\nElapsed: 01:31\n";
        var read = BattleText.Parse(panel);
        Assert.True(read.FrameGood);
        Assert.Equal(28, read.Kills);
        Assert.True(read.KillsFromMob);
        Assert.Equal(76_211, read.Damage);
        Assert.Equal(4_042, read.Exp);
        Assert.Equal(3_645, read.Meso);
        Assert.Equal(91_000, read.DurationMs);

        var garbled = BattleText.Parse("Battle Analysis | Dmg: | Mob: | Exp: | Mesot | 76,211 (834/s) | 28 (181m) | 4,042 (2,657/m) | 3,645 (2,396/m) | Elapsed: 01:31");
        Assert.True(garbled.FrameGood);
        Assert.Equal(28, garbled.Kills);
        Assert.Equal(91_000, garbled.DurationMs);

        var mobIsDamage = BattleText.Parse("Dmg:\nMob:\nExp:\nMeso:\n475 (5/m)\n2.16M (440/s)\n87,392\n40,000\n");
        Assert.False(mobIsDamage.FrameGood);
        Assert.NotEqual(475, mobIsDamage.Kills);
        Assert.NotEqual(2_160_000, mobIsDamage.Kills);

        var watch = new TrainingWatch();
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Idle,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 1_000,
        });
        watch.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 2_000,
            Battle = read,
        });
        Assert.Equal(0, watch.Shown!.Kills);
        Assert.Equal(76_211, watch.Shown.Damage);
        Assert.Equal(4_042, watch.Shown.Exp);
        Assert.Equal(3_645, watch.Shown.Meso);
        Assert.Equal(91_000, watch.Shown.DurationMs);
        Assert.Equal("kept", watch.BattleKillUse);

        var joined = new TrainingWatch();
        joined.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 1_000,
        });
        Assert.Equal(0, joined.Shown!.Kills);
        joined.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            CharacterName = "ExtraSin",
            AtMs = 2_000,
            Battle = BattleText.Parse("Battle Analysis | Dmg: | Mob: | Exp: | Meso: | 76,211 (834/s) | 28 (181m) | 4,042 (2,657/m) | 3,645 (2,396/m) | Elapsed: 01:31"),
        });
        Assert.Equal(0, joined.Shown.Kills);
        Assert.Equal(76_211, joined.Shown.Damage);
        Assert.Equal(4_042, joined.Shown.Exp);
        Assert.Equal(3_645, joined.Shown.Meso);
        Assert.Equal(91_000, joined.Shown.DurationMs);

        var bare = BattleText.Parse("151,037 (158/s) | 52 (31m) | 7,498 (4731m) | 46,578 (53,505/m)");
        Assert.True(bare.FrameGood);
        Assert.Equal(52, bare.Kills);
        joined.Observe(new TrainingSignal
        {
            Analysis = AnalysisSignal.Running,
            MapName = "Entrance to Sky Forest",
            AtMs = 3_000,
            Battle = bare,
        });
        Assert.Equal(24, joined.Shown.Kills);
        Assert.Equal(151_037, joined.Shown.Damage);
    }

    [Fact]
    public void A_shifted_column_read_does_not_call_the_exp_row_the_mob_count()
    {
        var shifted = BattleText.Parse("Dmg:\nMob: 2.12M (456/s)\nExp: 460 (5/m)\nMeso: 86,966 (1,123/m)\n");
        Assert.Null(shifted.Kills);
        Assert.NotEqual(460, shifted.Kills);
        Assert.NotEqual(86_966, shifted.Kills);
        Assert.Equal(2_120_000, shifted.Damage);
        Assert.Equal(460, shifted.Exp);

        var panel = BattleText.Parse("Battle Analysis\nDmg:\nMob:\nExp:\nMeso:\n2.12M (4 Is)\n87,392 (1,12 m)\n");
        Assert.Null(panel.Kills);
        Assert.NotEqual(87_392, panel.Kills);
        Assert.Equal(2_120_000, panel.Damage);

        Assert.Equal(460, BattleText.Parse("Mob: 460 (5/m)").Kills);
        Assert.Equal("Mob", BattleColumns.NameFromLabel("Mob:"));
        Assert.Equal("Dmg", BattleColumns.NameFromLabel("Dmg"));
        Assert.Null(BattleColumns.NameFromLabel("Battle Analysis"));
    }

    [Fact]
    public void Broken_loot_ocr_keeps_mesos_and_drops_the_fragments()
    {
        var mesos = DropLines.Parse("You have gained mesos (+137).");
        Assert.NotNull(mesos);
        Assert.Equal("Mesos", mesos.ItemName);
        Assert.Equal(137, mesos.Quantity);
        Assert.Equal(112, DropLines.Parse("You have gained mesos +112.")!.Quantity);
        Assert.Null(DropLines.Parse("You have gained exper."));
        Assert.Null(DropLines.Parse("You have gained ex erience +6."));
        Assert.Null(DropLines.Parse("You have gained mesos f."));
        Assert.Null(DropLines.Parse("E*traCold"));

        var tab = DropLines.Parse("You have gained an item in the Etc tab (Orange Potion).");
        Assert.NotNull(tab);
        Assert.Equal("Orange Potion", tab.ItemName);
        Assert.Equal("Cursed Doll", DropLines.Parse("You have gained an item in the Etc tab (Cursed Doll")!.ItemName);
        Assert.Equal("Lupin's Banana", DropLines.Parse("You have gained an item in'the Etc tab (Lupin's Banana")!.ItemName);
        Assert.Equal("Stiff Feather", DropLines.Parse("You have gained an item in the Etc tab (Stiff Feather")!.ItemName);
        Assert.Equal("Blue Potion", DropLines.Parse("You have gained in the (Blue Potion")!.ItemName);
        Assert.Equal(186, DropLines.Parse("You have gained mesos ( * 186")!.Quantity);

        var first = DropLines.Ingest(new NoticeCursor(),
        [
            new NoticeFrame
            {
                RegionHash = "a",
                Lines = ["You have gained mesos (+100).", "You have gained exper."],
            },
        ]);
        Assert.Equal("Mesos", Assert.Single(first.Drops).ItemName);
        Assert.Equal(100, first.Drops[0].Quantity);

        var again = DropLines.Ingest(first.Cursor,
        [
            new NoticeFrame
            {
                RegionHash = "b",
                Lines = ["You have gained mesos +100."],
            },
        ]);
        Assert.Empty(again.Drops);
    }

    [Fact]
    public void Battle_ink_keeps_dark_text_and_the_rows_stay_in_order()
    {
        Assert.True(BattleInk.IsInk(28, 28, 28));
        Assert.True(BattleInk.IsInk(90, 90, 90));
        Assert.False(BattleInk.IsInk(236, 232, 220));
        Assert.False(BattleInk.IsInk(40, 160, 50));
        Assert.False(BattleInk.IsInk(70, 190, 60));

        const int width = 220;
        const int height = 150;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 236, 232, 220);
        PaintRect(pixels, width, 4, 4, 18, 10, 40, 160, 50);
        PaintRect(pixels, width, 10, 8, 48, 6, 28, 28, 28);
        PaintRect(pixels, width, 168, 112, 40, 14, 70, 190, 60);
        var rows = new[] { 28, 48, 68, 88, 108 };
        foreach (var row in rows)
        {
            PaintRect(pixels, width, 10, row, 24, 8, 24, 24, 24);
            PaintRect(pixels, width, 130, row, 36, 8, 20, 20, 20);
        }

        var ink = BattleInk.Isolate(pixels, width, height);
        Assert.Equal(0, ink[(28 * width + 14) * 4]);
        Assert.Equal(255, ink[(20 * width + 14) * 4]);
        Assert.Equal(255, ink[(6 * width + 8) * 4]);
        Assert.Equal(255, ink[(116 * width + 180) * 4]);

        var columns = BattleColumns.Slice(pixels, width, height);
        Assert.Equal(["Dmg", "Mob", "Exp", "Meso", "Elapsed"], columns.Select(column => column.Name).ToArray());
        var mob = columns[1];
        var damage = columns[0];
        Assert.InRange(mob.SourceY, 48, 55);
        Assert.True(mob.SourceX >= 100);
        Assert.InRange(damage.SourceY, 28, 35);
        Assert.True(damage.SourceY + damage.SourceHeight < mob.SourceY);
        Assert.True(mob.SourceX + mob.SourceWidth < 168);
    }

    [Fact]
    public void Sky_forest_frame_puts_the_mob_number_on_the_mob_row()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "battle-window.png");
        using var bitmap = new Bitmap(path);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = new byte[width * height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.True(BattleInk.IsInk(19, 24, 28));
        Assert.True(BattleInk.IsInk(42, 71, 23));
        Assert.False(BattleInk.IsInk(96, 88, 65));
        Assert.False(BattleInk.IsInk(209, 243, 157));

        var columns = BattleColumns.Slice(pixels, width, height);
        Assert.True(columns.Count >= 4);
        Assert.Equal(["Dmg", "Mob", "Exp", "Meso"], columns.Take(4).Select(column => column.Name).ToArray());
        var damage = columns[0];
        var mob = columns[1];
        var exp = columns[2];
        Assert.True(damage.SourceY + damage.SourceHeight <= mob.SourceY);
        Assert.True(mob.SourceY + mob.SourceHeight <= exp.SourceY);
        Assert.True(mob.SourceX > width / 3);
        Assert.True(damage.SourceX > width / 3);
        Assert.True(mob.SourceY > damage.SourceY + 8);

        var shifted = BattleText.Parse("Dmg:\nMob: 2.11M (455/s)\nExp: 459 (5/m)\nMeso: 86,824 (1,122/m)\n");
        Assert.Null(shifted.Kills);
        Assert.Equal(2_110_000, shifted.Damage);
        Assert.Equal(459, shifted.Exp);

        var fixture = BattleText.Parse("Battle Analysis\nDmg:\nMob:\nExp.\nMeso:\n2.18M (441/5,\n480 (51m:\n89,860 (1,093/10\n47,157 (5741m:");
        Assert.True(fixture.FrameGood, fixture.FrameReason + " dmg=" + fixture.Damage + " mob=" + fixture.Kills + " exp=" + fixture.Exp + " meso=" + fixture.Meso);
        Assert.Equal(2_180_000, fixture.Damage);
        Assert.Equal(480, fixture.Kills);
        Assert.Equal(89_860, fixture.Exp);
        Assert.Equal(47_157, fixture.Meso);

        var dropped = BattleText.Parse("Battle Analysis\nMob:\nExp:\nMeso:\n2.20M (444/s)\n487 (5/m)\n90,872 (1,104/m)\n47,897 (582/m)\nElapsed: 82:15");
        Assert.True(dropped.FrameGood);
        Assert.Equal(2_200_000, dropped.Damage);
        Assert.Equal(487, dropped.Kills);
        Assert.Equal(90_872, dropped.Exp);
        Assert.Equal(47_897, dropped.Meso);
        Assert.Equal((82 * 60 + 15) * 1000L, dropped.DurationMs);
    }

    [Fact]
    public void Loot_ink_keeps_white_glyphs_and_drops_the_bright_map()
    {
        Assert.True(LootInk.IsInk(252, 242, 240));
        Assert.True(LootInk.IsInk(255, 255, 255));
        Assert.False(LootInk.IsInk(40, 140, 50));
        Assert.False(LootInk.IsInk(215, 250, 217));
        Assert.False(LootInk.IsInk(255, 180, 40));

        var pixels = new byte[8];
        pixels[0] = 50;
        pixels[1] = 140;
        pixels[2] = 40;
        pixels[3] = 255;
        pixels[4] = 240;
        pixels[5] = 242;
        pixels[6] = 252;
        pixels[7] = 255;
        var ink = LootInk.Isolate(pixels, 2, 1);
        Assert.Equal(0, ink[0]);
        Assert.Equal(255, ink[4]);
        Assert.Equal(255, ink[6]);
    }

    [Fact]
    public void Loot_notice_keeps_gray_text_on_a_bright_sky_and_drops_the_sky()
    {
        const int width = 24;
        const int height = 16;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 232, 232, 232);
        for (var y = 4; y <= 12; y++)
        {
            for (var x = 6; x <= 10; x++) Set(pixels, width, x, y, 160, 160, 160);
        }

        Set(pixels, width, 2, 2, 255, 180, 40);

        var flat = LootInk.Isolate(pixels, width, height);
        Assert.Equal(255, flat[0]);
        Assert.Equal(0, flat[(6 * width + 8) * 4]);

        var notice = LootInk.IsolateNotice(pixels, width, height);
        Assert.Equal(0, notice[(8 * width + 22) * 4]);
        Assert.Equal(255, notice[(6 * width + 8) * 4]);
        Assert.Equal(0, notice[(2 * width + 2) * 4]);

        var rows = LootInk.TextRows(notice, width, height);
        Assert.Single(rows);
        Assert.InRange(rows[0].Top, 3, 6);
        Assert.InRange(rows[0].Bottom, 11, 15);
    }

    [Fact]
    public void Loot_notice_rows_split_two_lines_and_skip_a_tall_blob()
    {
        const int width = 40;
        const int height = 60;
        var ink = new byte[width * height * 4];
        for (var y = 4; y < 12; y++)
        {
            for (var x = 2; x < 30; x++) Set(ink, width, x, y, 255, 255, 255);
        }

        for (var y = 20; y < 28; y++)
        {
            for (var x = 2; x < 30; x++) Set(ink, width, x, y, 255, 255, 255);
        }

        for (var y = 36; y < 60; y++)
        {
            for (var x = 2; x < 10; x++) Set(ink, width, x, y, 255, 255, 255);
        }

        var rows = LootInk.TextRows(ink, width, height);
        Assert.Equal(2, rows.Count);
        Assert.Equal((4, 12), rows[0]);
        Assert.Equal((20, 28), rows[1]);
    }

    [Fact]
    public void Pounder_loot_crop_keeps_a_text_row_and_not_a_white_sheet()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "loot-pounder.png");
        using var bitmap = new Bitmap(path);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = new byte[width * height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var notice = LootInk.IsolateNotice(pixels, width, height);
        var white = 0;
        var best = 0;
        for (var y = 0; y < height; y++)
        {
            var row = 0;
            for (var x = width / 2; x < width; x++)
            {
                if (notice[(y * width + x) * 4] == 0) continue;
                white++;
                row++;
            }

            if (row > best) best = row;
        }

        Assert.InRange(best, 40, width / 2);
        Assert.True(white < width * height / 8, "notice ink " + white);
        var rows = LootInk.TextRows(notice, width, height);
        Assert.True(rows.Count >= 4, "text rows " + rows.Count);
    }

    [Fact]
    public void Map_title_image_drops_the_forest_and_enlarges_the_letters()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 160;
            pixels[i + 2] = 50;
            pixels[i + 3] = 255;
        }

        var white = (1 * 4 + 1) * 4;
        pixels[white] = 255;
        pixels[white + 1] = 255;
        pixels[white + 2] = 255;
        pixels[white + 3] = 255;

        var prepared = MapTitleImage.Prepare(pixels, 4, 4, out var width, out var height);
        Assert.Equal(12, width);
        Assert.Equal(12, height);
        Assert.Equal(0, prepared[0]);
        var letter = ((3 * width) + 3) * 4;
        Assert.Equal(255, prepared[letter]);
        Assert.Equal(255, prepared[letter + 1]);
        Assert.Equal(255, prepared[letter + 2]);

        var forest = MapTitleImage.Prepare(new byte[4 * 4 * 4], 4, 4, out var blankWidth, out var blankHeight);
        Assert.Equal(1, blankWidth);
        Assert.Equal(1, blankHeight);
        Assert.Equal(0, forest[0]);
    }

    [Fact]
    public void Panel_read_of_entrance_beats_the_ink_mask_that_splits_the_word()
    {
        var chosen = MapTitle.Prefer(
            ["Korean Folk Town", "Entrance to Black Mountain"],
            ["Korean Folk Town", "Entra nce to Black Mountain"]);
        var read = MapTitle.Choose(chosen);
        Assert.NotNull(read);
        Assert.True(read.Value.FromCatalog);
        Assert.Equal("Entrance to Black Mountain", read.Value.Name);
        Assert.Equal(222010000, read.Value.MapId);

        var painted = MapTitle.Prefer(
            ["Monkey ForeWll"],
            ["Monkey Forest II"]);
        var forest = MapTitle.Choose(painted);
        Assert.NotNull(forest);
        Assert.True(forest.Value.FromCatalog);
        Assert.Equal("Monkey Forest II", forest.Value.Name);
        Assert.Equal(100040103, forest.Value.MapId);
    }

    [Fact]
    public void A_thin_roman_numeral_still_names_the_map_and_a_smashed_word_does_not()
    {
        var read = MapTitle.Choose(["Monkey Forest ll"]);
        Assert.NotNull(read);
        Assert.True(read.Value.FromCatalog);
        Assert.Equal("Monkey Forest II", read.Value.Name);
        Assert.Equal(100040103, read.Value.MapId);

        var smashed = MapTitle.Choose(["Monkey ForeWll"]);
        Assert.NotNull(smashed);
        Assert.False(smashed.Value.FromCatalog);
    }

    [Fact]
    public void A_release_companion_does_not_keep_the_local_dev_site()
    {
        Assert.Equal("https://osmbuddy.apps.grok.bot/api/farm/feed", WebsiteAddress.LiveFeed);
        Assert.True(WebsiteAddress.IsLoopback("http://127.0.0.1:4317/api/farm/feed"));
        Assert.True(WebsiteAddress.IsLoopback("http://localhost:4317/api/farm/feed"));
        Assert.False(WebsiteAddress.IsLoopback(WebsiteAddress.LiveFeed));
        Assert.Equal("https://osmbuddy.apps.grok.bot/api/companion/logs", WebsiteAddress.LogsUrl(WebsiteAddress.LiveFeed));
        Assert.Equal("http://127.0.0.1:4317/api/companion/logs", WebsiteAddress.LogsUrl(WebsiteAddress.DevFeed));
        Assert.Equal("https://github.com/EmanuelKedmi/osmbuddy-companion/releases/latest/download/MarketNotes.Companion.exe", WebsiteAddress.ReleaseDownloadUrl);
        Assert.Equal("1.0.2", WebsiteAddress.VersionFromReleaseTag("v1.0.2"));
        Assert.Equal("1.0.2", WebsiteAddress.VersionFromReleaseTag("1.0.2"));
        Assert.Null(WebsiteAddress.VersionFromReleaseTag("latest"));
    }

    [Fact]
    public void Clipped_drop_names_use_the_prefix_and_the_map()
    {
        Assert.Equal("Orange Potion", DropLines.Parse("You have gained an Orange Pot.")!.ItemName);
        Assert.Equal("Red Potion", DropLines.Parse("You have gained Red Potions.")!.ItemName);
        Assert.Equal("Scroll for Helmet for DEX 60%", DropLines.Parse("You have gained a Scroll for Helmet for DEX 60.")!.ItemName);
        Assert.Null(DropLines.Parse("You have gained a Scroll for Helmet for DEX."));
        Assert.Null(DropLines.Parse("You have gained a Raccoon Mask."));
        Assert.Null(DropLines.Parse("E*traCold"));
        Assert.Null(DropLines.Parse("You have gained exper."));

        const string clipped = "You have gained a Scroll for Helmet for DEX.";
        Assert.Equal("Scroll for Helmet for DEX 60%", DropLines.Parse(clipped, 211041500)!.ItemName);
        Assert.Equal("Scroll for Helmet for DEX 60%", DropLines.Parse(clipped, mapName: "Dead Mine I")!.ItemName);
        Assert.Null(DropLines.Parse("You have gained a Scroll for Earring for DEX.", 211040700));
        Assert.Equal("Scroll for Earring for LUK 10%", DropLines.Parse("You have gained a Scroll for Earring for LUK.", 211040101)!.ItemName);
        Assert.Null(DropLines.Parse(clipped, 104030001));

        Assert.False(PriceCatalog.TryMatch("Orange Pot", out _));
        Assert.False(PriceCatalog.TryMatch("332 602", out _));
        Assert.False(PriceCatalog.TryMatch("Raccoon Mask", out _));
        Assert.True(PriceCatalog.TryMatch("Scroll for Helmet for DEX", out var shop));
        Assert.Equal("Scroll for Helmet for DEF", shop);

        var waiting = DropLines.Ingest(new NoticeCursor(),
        [
            new NoticeFrame { RegionHash = "a", Lines = [clipped] },
        ]);
        Assert.Empty(waiting.Drops);

        var confirmed = DropLines.Ingest(waiting.Cursor,
        [
            new NoticeFrame { RegionHash = "a", Lines = [clipped] },
        ], 211041500);
        Assert.Equal("Scroll for Helmet for DEX 60%", Assert.Single(confirmed.Drops).ItemName);

        var repeat = DropLines.Ingest(confirmed.Cursor,
        [
            new NoticeFrame { RegionHash = "a", Lines = [clipped] },
        ], 211041500);
        Assert.Empty(repeat.Drops);
        Assert.Equal(1, repeat.Skipped);

        var session = new TrainingSession();
        session.BeginLive("ExtraCold", null, "", 1, false, new BattleSnapshot(), 1_000);
        session.Ingest([new NoticeFrame { RegionHash = "loot", Lines = [clipped] }]);
        Assert.Empty(session.Drops);
        session.RememberMap("Dead Mine I", 211041500);
        Assert.Equal(1, Assert.Single(session.Drops).Quantity);
        Assert.Equal("Scroll for Helmet for DEX 60%", session.Drops[0].ItemName);
        session.Ingest([new NoticeFrame { RegionHash = "loot-2", Lines = [clipped] }]);
        Assert.Equal(1, session.Drops[0].Quantity);
    }

    private static FmVisibleRow Offer(string item, long price, bool sold = false) => new()
    {
        Item = item,
        Quantity = 1,
        PriceEach = price,
        Slot = 0,
        Row = 0,
        Sold = sold,
    };

    private static void Paint(byte[] pixels, int stridePixels, int y0, int y1, byte r, byte g, byte b)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = 0; x < stridePixels; x++)
            {
                Set(pixels, stridePixels, x, y, r, g, b);
            }
        }
    }

    private static FmFrameInput ShopFrame(string owner, string shop, params object[] rows)
    {
        var visible = new List<FmVisibleRow>();
        for (var i = 0; i < rows.Length; i += 2)
        {
            visible.Add(new FmVisibleRow
            {
                Item = (string)rows[i],
                Quantity = 1,
                PriceEach = Convert.ToInt64(rows[i + 1], System.Globalization.CultureInfo.InvariantCulture),
                Slot = visible.Count,
                Row = visible.Count,
            });
        }

        return new FmFrameInput
        {
            MapName = "Free Market<2>",
            Owner = owner,
            ShopName = shop,
            Channel = 1,
            Scrollbar = new FmScrollbar { Position = 0, First = 0, Visible = visible.Count },
            Rows = visible,
        };
    }

    private sealed record PaintedBattle(byte[] Pixels, int Width, int Height, int PanelX, int PanelY, int PanelW, int PanelH, int ButtonX, int ButtonY, int ButtonW, int ButtonH);

    /// <summary>
    /// Sky behind a light title strip and a solid START or CANCEL control.
    /// The body stays sky-colored so the finder has to use the title bar.
    /// A leaf-green block and a left-side orange block are decoys.
    /// </summary>
    private static PaintedBattle PaintBattle(int width, int height, int panelX, int panelY, int buttonH, bool cancel)
    {
        var buttonW = buttonH * 4;
        var panelW = buttonW * 3;
        var panelH = buttonH * 8;
        var inset = Math.Max(4, buttonH / 2);
        var buttonX = panelX + panelW - inset - buttonW;
        var buttonY = panelY + panelH - inset - buttonH;
        var pixels = new byte[width * height * 4];
        Fill(pixels, 30, 90, 200);
        PaintRect(pixels, width, 8, 30, 88, 40, 70, 160, 62);
        PaintRect(pixels, width, 16, panelY + panelH, 72, 22, 230, 140, 40);
        PaintRect(pixels, width, panelX, panelY, panelW, buttonH, 244, 246, 248);
        var inkTop = panelY + Math.Max(1, buttonH / 5);
        var inkH = Math.Max(2, buttonH / 2);
        for (var col = panelX + 6; col < panelX + panelW - 6; col += 7)
        {
            PaintRect(pixels, width, col, inkTop, 2, inkH, 20, 20, 20);
        }

        PaintRect(pixels, width, buttonX, buttonY, buttonW, buttonH, (byte)(cancel ? 170 : 230), (byte)(cancel ? 204 : 140), (byte)(cancel ? 34 : 40));
        var holeY = buttonY + Math.Max(3, buttonH / 5);
        var holeH = Math.Max(2, buttonH / 3);
        var holeX = buttonX + Math.Max(4, buttonW / 8);
        var holeW = buttonW - Math.Max(8, buttonW / 4);
        if (holeH < buttonH - 4 && holeW > 4)
        {
            PaintRect(pixels, width, holeX, holeY, holeW, holeH, 20, 20, 20);
        }

        return new PaintedBattle(pixels, width, height, panelX, panelY, panelW, panelH, buttonX, buttonY, buttonW, buttonH);
    }

    private static void AssertOnPanel(PixelRect? window, PaintedBattle placed)
    {
        Assert.NotNull(window);
        var box = window!.Value;
        Assert.InRange(box.X, placed.PanelX - 16, placed.PanelX + 8);
        Assert.InRange(box.Y, placed.PanelY - 16, placed.PanelY + 8);
        Assert.InRange(Math.Abs(box.Width - placed.PanelW), 0, Math.Max(24, placed.PanelW / 5));
        Assert.InRange(Math.Abs(box.Height - placed.PanelH), 0, Math.Max(24, placed.PanelH / 5));
        Assert.True(box.X < placed.ButtonX);
        Assert.True(box.Y < placed.ButtonY);
        Assert.True(box.X + box.Width >= placed.ButtonX + placed.ButtonW);
        Assert.True(box.Y + box.Height >= placed.ButtonY + placed.ButtonH);
    }

    private static void PaintRect(byte[] pixels, int stridePixels, int x, int y, int width, int height, byte r, byte g, byte b)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var col = x; col < x + width; col++)
            {
                Set(pixels, stridePixels, col, row, r, g, b);
            }
        }
    }

    private static void Fill(byte[] pixels, byte r, byte g, byte b)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
    }

    private static void DrawPanel(byte[] pixels, int stridePixels, int x, int y, int width, int height, bool varied, bool scrollbar, double thumbAt, bool bright = false)
    {
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                if (varied)
                {
                    var on = ((col / 8) + (row / 8)) % 2 == 0;
                    if (bright)
                    {
                        Set(pixels, stridePixels, x + col, y + row, (byte)(on ? 200 : 245), (byte)(on ? 210 : 248), (byte)(on ? 220 : 250));
                    }
                    else
                    {
                        Set(pixels, stridePixels, x + col, y + row, (byte)(on ? 30 : 210), (byte)(on ? 90 : 220), (byte)(on ? 40 : 230));
                    }
                }
                else
                {
                    Set(pixels, stridePixels, x + col, y + row, 248, 248, 248);
                }
            }
        }

        for (var col = 0; col < width; col++)
        {
            Set(pixels, stridePixels, x + col, y, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
            Set(pixels, stridePixels, x + col, y + 1, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
            Set(pixels, stridePixels, x + col, y + height - 1, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
            Set(pixels, stridePixels, x + col, y + height - 2, UiChromeFinder.FrameR, UiChromeFinder.FrameG, UiChromeFinder.FrameB);
        }

        if (!scrollbar) return;
        var trackX = x + (int)(width * 0.46);
        var trackTop = y + 28;
        var trackBottom = y + height - 8;
        for (var row = trackTop; row < trackBottom; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                Set(pixels, stridePixels, trackX + col, row, UiChromeFinder.TrackR, UiChromeFinder.TrackG, UiChromeFinder.TrackB);
            }
        }

        var thumbHeight = 36;
        var room = (trackBottom - trackTop) - thumbHeight;
        var thumbTop = trackTop + (int)(room * thumbAt);
        for (var row = thumbTop; row < thumbTop + thumbHeight; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                Set(pixels, stridePixels, trackX + col, row, UiChromeFinder.ThumbR, UiChromeFinder.ThumbG, UiChromeFinder.ThumbB);
            }
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
