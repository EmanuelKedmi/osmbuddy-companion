using System.Drawing;
using System.Drawing.Imaging;
using MarketNotes.Companion.Overlay;
using MarketNotes.Companion.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace MarketNotes.Companion.Tests;

public class HoverCardTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Price_trend_names_the_low_the_latest_and_the_high()
    {
        var read = new HoverRead
        {
            ItemName = "Scroll for Claw for ATT 60%",
            Quote = PriceCatalog.Lookup("Scroll for Claw for ATT 60%"),
            ListingPrices = [400_000, 1_200_000, 2_000_000],
            Trend =
            [
                Point("2026-01-01T00:00:00.0000000Z", 400_000),
                Point("2026-06-01T00:00:00.0000000Z", 2_000_000),
                Point("2026-03-01T00:00:00.0000000Z", 1_200_000),
            ],
        };

        var card = HoverSections.Compose(read, _ => true, _ => false);

        Assert.NotNull(card);
        Assert.Equal("Scroll for Claw for ATT 60%", card.ItemName);
        var trend = Assert.Single(card.Sections, section => section.Id == "price-trend");
        Assert.Contains(trend.Lines, line => line.Text == "Discord market · sold · 2,000,000");
        Assert.Contains(trend.Lines, line => line.Text == "Median listing · 1,200,000");
        Assert.Contains(trend.Lines, line => line.Text == "Median sales · none yet");
        Assert.Contains(trend.Lines, line => line.Text == "Low 400,000 · latest 2,000,000 · high 2,000,000");
        Assert.Equal(new[] { 0d, 0.5, 1d }, trend.Trend);
    }

    [Fact]
    public void Median_listing_and_sales_leave_out_exaggerated_prices()
    {
        var read = new HoverRead
        {
            ItemName = "Scroll for Claw for ATT 60%",
            Quote = PriceCatalog.Lookup("Scroll for Claw for ATT 60%"),
            ListingPrices = [400_000, 1_200_000, 2_000_000, 1_000_000_000],
            SalePrices = [900_000, 1_100_000, 2_147_483_647, 1],
            Trend =
            [
                Point("2026-01-01T00:00:00.0000000Z", 400_000),
                Point("2026-03-01T00:00:00.0000000Z", 1_200_000),
                Point("2026-04-01T00:00:00.0000000Z", 2_000_000),
                Point("2026-06-01T00:00:00.0000000Z", 1_000_000_000),
                Sale("2026-02-01T00:00:00.0000000Z", 900_000),
                Sale("2026-02-15T00:00:00.0000000Z", 1_100_000),
                Sale("2026-05-01T00:00:00.0000000Z", 2_147_483_647),
                Sale("2026-05-02T00:00:00.0000000Z", 1),
            ],
        };

        var trend = Assert.Single(HoverSections.Compose(read, _ => true, _ => false)!.Sections, section => section.Id == "price-trend");
        Assert.Contains(trend.Lines, line => line.Text == "Median listing · 1,200,000");
        Assert.Contains(trend.Lines, line => line.Text == "Median sales · 1,000,000");
        Assert.Contains(trend.Lines, line => line.Text == "Low 400,000 · latest 2,000,000 · high 2,000,000");
        Assert.DoesNotContain(trend.Lines, line => line.Text.Contains("1,000,000,000", StringComparison.Ordinal));

        Assert.Null(PriceMedian.Of([1L, 50_000_000L]));
        Assert.Equal(500_000, PriceMedian.Of([100L, 400_000L, 500_000L, 600_000L]));
        Assert.Equal(5_100, PriceMedian.Of([4_800L, 5_000, 5_000, 5_200, 5_400, 5_600, 20_000_000, 500_000_000]));
        Assert.Equal(2_250_000, PriceMedian.Of([1_000_000L, 1_200_000, 1_500_000, 2_000_000, 2_500_000, 3_000_000, 4_000_000, 8_000_000]));
    }

    [Fact]
    public void The_website_scan_supplies_listing_and_sale_prices()
    {
        const string json = """
            {
              "ok": true,
              "scan": {
                "shops": [
                  {
                    "open": true,
                    "lines": [
                      { "item": "DEX Crystal Ore", "priceEach": 4000, "status": "listed" },
                      { "item": "DEX Crystal Ore", "priceEach": 6000, "status": "listed" },
                      { "item": "DEX Crystal Ore", "priceEach": 1000000000, "status": "listed" },
                      { "item": "DEX Crystal Ore", "priceEach": 9000, "status": "deleted" }
                    ]
                  },
                  {
                    "open": false,
                    "lines": [ { "item": "DEX Crystal Ore", "priceEach": 50000, "status": "listed" } ]
                  }
                ],
                "sold": [
                  { "item": "DEX Crystal Ore", "priceEach": 3500 },
                  { "item": "DEX Crystal Ore", "priceEach": 4500 },
                  { "item": "DEX Crystal Ore", "priceEach": 1 }
                ]
              }
            }
            """;

        var items = FmMarket.Read(json);
        var ore = items[FmMarket.Key("dex crystal ore")];
        Assert.Equal(5_000, PriceMedian.Of(ore.Listings));
        Assert.Equal(4_000, PriceMedian.Of(ore.Sales));
        Assert.Contains(1_000_000_000, ore.Listings);
        Assert.DoesNotContain(50_000, ore.Listings);
        Assert.DoesNotContain(9_000, ore.Listings);

        var direct = FmMarket.ReadItem("""{"ok":true,"listings":[4000,6000],"sales":[3500]}""", "DEX Crystal Ore");
        Assert.Equal([4000L, 6000L], direct.Listings);
        Assert.Equal([3500L], direct.Sales);
    }

    [Fact]
    public void Shops_stay_collapsed_until_the_card_is_expanded()
    {
        var read = ReadWithShop();
        var collapsed = HoverSections.Compose(read, _ => true, _ => false);
        var shops = Assert.Single(collapsed!.Sections, section => section.Id == "active-shops");
        Assert.Equal("1 active shop", Assert.Single(shops.Lines).Text);
        Assert.True(shops.CanExpand);
        Assert.Equal("Show shops", shops.ExpandLabel);

        var open = HoverSections.Compose(read, _ => true, id => id == "active-shops");
        var expanded = Assert.Single(open!.Sections, section => section.Id == "active-shops");
        Assert.Contains(expanded.Lines, line => line.Text == "Kate · Scrolls · Free Market<1> · 1,500,000 × 2");
        Assert.Equal("Hide shops", expanded.ExpandLabel);
    }

    [Fact]
    public void Gear_rolls_render_beside_the_price_and_stay_quiet_without_a_range()
    {
        var bare = HoverSections.Compose(ReadWithShop(), _ => true, _ => false);
        Assert.DoesNotContain(bare!.Sections, section => section.Id == "gear-rolls");

        var read = ReadWithShop();
        read.Rolls =
        [
            new GearRoll("STR", 3, 6, 9),
            new GearRoll("DEX", 1, 2, 4),
        ];
        var card = HoverSections.Compose(read, _ => true, _ => false);
        Assert.Equal(["price-trend", "active-shops", "gear-rolls"], card!.Sections.Select(section => section.Id));
        var rolls = card.Sections[^1];
        Assert.Equal("Rolls", rolls.Title);
        Assert.Equal("STR · min 3 · avg 6 · max 9", rolls.Lines[0].Text);
        Assert.Equal("DEX · min 1 · avg 2 · max 4", rolls.Lines[1].Text);

        var orientican = GearRolls.For("Blue Orientican");
        Assert.Contains(orientican, roll => roll == new GearRoll("STR", 0, 1, 2));
        Assert.Contains(orientican, roll => roll == new GearRoll("DEX", 3, 4, 5));
        Assert.Contains(orientican, roll => roll == new GearRoll("Weapon Def", 58, 65, 72));
        var khanjar = GearRolls.For("Khanjar");
        Assert.Contains(khanjar, roll => roll == new GearRoll("Weapon Attack", 6, 7, 8));
        Assert.Contains(khanjar, roll => roll == new GearRoll("LUK", 0, 1, 2));
        Assert.Empty(GearRolls.For("Scroll for Claw for ATT 60%"));
        Assert.Empty(GearRolls.For("Diamond Ore"));
        Assert.Equal(5, GearRolls.Span(85, 5));
        Assert.Equal(7, GearRolls.Span(65, 10));
    }

    [Fact]
    public void A_disabled_section_is_left_out_of_the_card()
    {
        var card = HoverSections.Compose(ReadWithShop(), id => id != "price-trend", _ => false);
        Assert.DoesNotContain(card!.Sections, section => section.Id == "price-trend");
        Assert.Contains(card.Sections, section => section.Id == "active-shops");
    }

    [Fact]
    public void The_card_flips_to_the_left_when_the_right_side_would_leave_the_client()
    {
        var beside = NotesLayout.BesideTooltip(700, 40, 80, 120, 800, 600, 260, 180);
        Assert.Equal(700 - 8 - 260, beside.X);
        Assert.Equal(40, beside.Y);

        var room = NotesLayout.BesideTooltip(20, 40, 80, 120, 800, 600, 260, 180);
        Assert.Equal(20 + 80 + 8, room.X);

        var panel = NotesLayout.Panel(800, 600, 280, 200);
        Assert.Equal(800 - 280 - 16, panel.X);
        Assert.Equal(16, panel.Y);
    }

    [Theory]
    [InlineData("tooltip-diamond-ore.jpg", 586, 282, 589, 286, 868, 410)]
    [InlineData("tooltip-power-elixir.png", 288, 482, 290, 494, 760, 698)]
    [InlineData("tooltip-khanjar.png", 236, 198, 234, 201, 620, 640)]
    [InlineData("tooltip-red-sauna-robe.jpg", 490, 520, 492, 522, 737, 786)]
    [InlineData("tooltip-dex-crystal-ore.jpg", 230, 310, 238, 236, 700, 398)]
    public void The_item_tooltip_is_found_by_its_icon_square_over_any_background(
        string fixture, int cursorX, int cursorY, int left, int top, int right, int bottom)
    {
        var (pixels, width, height) = LoadFixture(fixture);
        var hit = ItemTooltipFinder.Find(pixels, width, height, width * 4, cursorX, cursorY);

        Assert.NotNull(hit);
        _output.WriteLine($"{fixture}: bounds {hit.Value.Bounds} icon {hit.Value.Icon} title {hit.Value.Title}");
        var slack = (int)Math.Ceiling(6 * hit.Value.Icon.Width / 64.0);
        Assert.InRange(hit.Value.Bounds.X, left - slack, left + slack);
        Assert.InRange(hit.Value.Bounds.Y, top - slack, top + slack);
        Assert.InRange(hit.Value.Bounds.Right, right - slack, right + slack);
        Assert.InRange(hit.Value.Bounds.Bottom, bottom - slack, bottom + slack);
        Assert.True(hit.Value.Title.Bottom <= hit.Value.Icon.Y);
        Assert.True(hit.Value.Title.Y > hit.Value.Bounds.Y);
    }

    [Fact]
    public void The_title_band_becomes_black_letters_on_white()
    {
        var (pixels, width, height) = LoadFixture("tooltip-red-sauna-robe.jpg");
        var hit = ItemTooltipFinder.Find(pixels, width, height, width * 4, 490, 520)!.Value;
        var ink = TooltipTitle.Ink(pixels, width, height, width * 4, hit.Title, 2);

        var dark = 0;
        for (var i = 0; i < ink.Bgra.Length; i += 4)
        {
            if (ink.Bgra[i] == 0) dark++;
        }

        Assert.Equal(hit.Title.Width * 2 + 16, ink.Width);
        Assert.InRange(dark, 400, ink.Width * ink.Height / 5);
    }

    [Theory]
    [InlineData("tooltip-diamond-ore.jpg", 560)]
    [InlineData("tooltip-red-sauna-robe.jpg", 480)]
    public void Inventory_slots_speech_bubbles_and_the_night_sky_are_not_a_tooltip(string fixture, int keepWidth)
    {
        var (pixels, width, height) = LoadFixture(fixture);
        var left = new byte[keepWidth * height * 4];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * width * 4, left, y * keepWidth * 4, keepWidth * 4);
        }

        for (var y = 40; y < height; y += 120)
        {
            for (var x = 40; x < keepWidth; x += 120)
            {
                Assert.Null(ItemTooltipFinder.Find(left, keepWidth, height, keepWidth * 4, x, y));
            }
        }
    }

    [Fact]
    public void The_title_line_is_the_item_and_a_stat_line_is_not()
    {
        Assert.Equal(
            "Scroll for Claw for ATT 60%",
            TooltipName.FromLines(["Scroll for Claw for ATT 60%", "Weapon Attack +3"]));
        Assert.Equal(
            "Scroll for Claw for ATT 60%",
            TooltipName.FromLines(["Weapon Attack +3", "Scroll for Claw for ATT 60%"]));
        Assert.Equal("Mystery Widget", TooltipName.FromLines(["Mystery Widget"]));
        Assert.Equal("Red Sauna Robe", TooltipName.FromLines(["Red Sauna Robe (+8)"]));
        Assert.Equal("DEX Crystal Ore", TooltipName.FromLines(["DEX Crystal Ore", "An ore of a crystal that possesses dexterity."]));
        Assert.Equal("LUK Crystal Ore", TooltipName.FromLines(["LUK : +1", "LUK Crystal Ore"]));
        Assert.Equal("Speed Potion", TooltipName.FromLines(["Speed Potion"]));
        Assert.Equal("Scroll for Claw for ATT 60%", TooltipName.FromLines(["CATEGORY : OVERALL", "Scroll for Claw for ATT 60%"]));
        Assert.Null(TooltipName.FromLines(["(OCR failed: IOException)"]));
    }

    [Fact]
    public void A_new_shop_price_stays_on_the_trend_and_only_the_new_ask_is_active()
    {
        var path = Path.Combine(Path.GetTempPath(), "hover-market-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var market = new HoverMarket(path);
            var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var second = first.AddDays(10);
            market.ObserveShop("Scroll for Claw for ATT 60%", "Kate", "Scrolls · Free Market<1>", 400_000, 1, first, sample: false);
            market.ObserveShop("Scroll for Claw for ATT 60%", "Kate", "Scrolls · Free Market<1>", 400_000, 3, first.AddDays(1), sample: false);
            market.ObserveShop("Scroll for Claw for ATT 60%", "Kate", "Scrolls · Free Market<1>", 1_500_000, 2, second, sample: false);
            market.ObserveSale("Scroll for Claw for ATT 60%", 900_000, first.AddDays(2), sample: false);
            market.ObserveShop("[Sample] Scroll", "Kate", "FM", 1, 1, first, sample: false);
            market.Save();

            var trend = market.Trend("Scroll for Claw for ATT 60%");
            Assert.Equal([400_000L, 900_000L, 1_500_000L], trend.Select(point => point.PriceEach));
            var active = Assert.Single(market.ActiveShops("scroll for claw for att 60%"));
            Assert.Equal(1_500_000, active.PriceEach);
            Assert.Equal(2, active.Quantity);

            market.CloseOwner("Kate");
            market.Save();
            Assert.Empty(market.ActiveShops("Scroll for Claw for ATT 60%"));

            var loaded = new HoverMarket(path);
            Assert.Empty(loaded.ActiveShops("Scroll for Claw for ATT 60%"));
            Assert.Equal(3, loaded.Trend("Scroll for Claw for ATT 60%").Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Notes_open_and_close_without_dropping_a_section()
    {
        var ui = new InGameUi();
        Assert.False(ui.Open);
        ui.Toggle();
        Assert.True(ui.Open);
        ui.SetEnabled("gear-rolls", false);
        Assert.False(ui.IsEnabled("gear-rolls"));
        ui.Toggle();
        Assert.False(ui.Open);
        Assert.True(ui.AnyEnabled);
        Assert.True(ui.IsEnabled("price-trend"));
        Assert.Contains(ui.Options, option => option.Id == "gear-rolls");
    }

    [Fact]
    public void The_card_waits_until_the_item_name_has_settled()
    {
        var hold = new NameHold();
        var reads = 0;
        string Read()
        {
            reads++;
            return reads == 1 ? "Dia" : "Diamond Ore";
        }

        Assert.Null(hold.Ready(1, frame: 1, Read));
        Assert.Null(hold.Ready(1, frame: 1, Read));
        Assert.Equal("Dia", hold.Ready(1, frame: 2, Read));
        Assert.Equal("Dia", hold.Ready(2, frame: 3, Read));
        Assert.Equal("Diamond Ore", hold.Ready(2, frame: 4, Read));
        Assert.Equal(2, reads);

        var flicker = new NameHold();
        Assert.Null(flicker.Ready(1, frame: 1, () => "Diamond Ore"));
        Assert.Equal("Diamond Ore", flicker.Ready(2, frame: 2, () => "Diamond Ore"));
        Assert.Equal("Diamond Ore", flicker.Ready(3, frame: 3, () => null));

        var retry = new NameHold();
        Assert.Null(retry.Ready(1, frame: 1, () => null));
        Assert.Null(retry.Ready(1, frame: 2, () => "Diamond Ore"));
        Assert.Equal("Diamond Ore", retry.Ready(1, frame: 3, () => null));
    }

    private static HoverRead ReadWithShop() => new()
    {
        ItemName = "Scroll for Claw for ATT 60%",
        Quote = PriceCatalog.Lookup("Scroll for Claw for ATT 60%"),
        Shops =
        [
            new HoverShop
            {
                Item = "Scroll for Claw for ATT 60%",
                Owner = "Kate",
                Place = "Scrolls · Free Market<1>",
                PriceEach = 1_500_000,
                Quantity = 2,
                At = "2026-06-01T00:00:00.0000000Z",
                Open = true,
            },
        ],
    };

    private static HoverPricePoint Point(string at, long price) => new()
    {
        Item = "Scroll for Claw for ATT 60%",
        PriceEach = price,
        At = at,
        Kind = "shop",
    };

    private static HoverPricePoint Sale(string at, long price) => new()
    {
        Item = "Scroll for Claw for ATT 60%",
        PriceEach = price,
        At = at,
        Kind = "sold",
    };

#pragma warning disable CA1416
    private static (byte[] Pixels, int Width, int Height) LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
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

        return (pixels, width, height);
    }
#pragma warning restore CA1416
}
