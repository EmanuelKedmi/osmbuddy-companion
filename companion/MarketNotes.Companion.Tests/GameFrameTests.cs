using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Scanning;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class GameFrameTests
{
    [Fact]
    public void Dpi_scaled_window_grab_is_the_client_and_the_white_glyph_stays_white()
    {
        const int windowWidth = 10;
        const int windowHeight = 8;
        const int clientX = 2;
        const int clientY = 2;
        const int clientWidth = 6;
        const int clientHeight = 4;
        var window = new byte[windowWidth * windowHeight * 4];
        Fill(window, 20, 20, 20);
        // Title bar pixel that must not survive.
        Set(window, windowWidth, 0, 0, 240, 240, 240);
        // White drop glyph inside the client.
        Set(window, windowWidth, clientX + 4, clientY + 3, 255, 255, 255);

        var scaled = ScaleNearest(window, windowWidth, windowHeight, 2);
        var fit = GameFrame.Fit(scaled, windowWidth * 2, windowHeight * 2, windowWidth, windowHeight, clientX, clientY, clientWidth, clientHeight);

        Assert.Equal(clientWidth, fit.Width);
        Assert.Equal(clientHeight, fit.Height);
        Assert.Equal(255, fit.Bgra[(3 * clientWidth + 4) * 4]);
        Assert.Equal(255, fit.Bgra[(3 * clientWidth + 4) * 4 + 1]);
        Assert.Equal(255, fit.Bgra[(3 * clientWidth + 4) * 4 + 2]);
        Assert.True(LootInk.IsInk(255, 255, 255));
        var ink = LootInk.Isolate(fit.Bgra, fit.Width, fit.Height);
        Assert.Equal(255, ink[(3 * clientWidth + 4) * 4]);
        // A filtered scale of that same white glyph falls through the ink floor.
        Assert.False(LootInk.IsInk(140, 150, 140));
    }

    [Fact]
    public void Letterboxed_grab_keeps_the_white_drop_line_for_loot_ink()
    {
        const int clientWidth = 80;
        const int clientHeight = 64;
        var client = new byte[clientWidth * clientHeight * 4];
        Fill(client, 40, 140, 50);
        var glyphX = (int)(clientWidth * 0.70);
        var glyphY = (int)(clientHeight * 0.80);
        Set(client, clientWidth, glyphX, glyphY, 255, 255, 255);
        Set(client, clientWidth, glyphX + 1, glyphY, 252, 252, 252);

        const int frameWidth = 200;
        const int frameHeight = 160;
        var frame = new byte[frameWidth * frameHeight * 4];
        for (var y = 0; y < clientHeight; y++)
        {
            Buffer.BlockCopy(client, y * clientWidth * 4, frame, y * frameWidth * 4, clientWidth * 4);
        }

        var fit = GameFrame.Fit(frame, frameWidth, frameHeight, frameWidth, frameHeight, 0, 0, frameWidth, frameHeight);

        Assert.Equal(clientWidth, fit.Width);
        Assert.Equal(clientHeight, fit.Height);
        var ink = LootInk.Isolate(fit.Bgra, fit.Width, fit.Height);
        Assert.Equal(255, ink[(glyphY * clientWidth + glyphX) * 4]);
        Assert.Equal(255, ink[(glyphY * clientWidth + glyphX + 1) * 4]);
        Assert.Equal(0, ink[0]);
    }

    [Fact]
    public void Clean_panel_from_this_client_sets_kills_and_a_damage_figure_does_not()
    {
        var parsed = BattleText.Parse("Dmg: 56,511 (90/s)\nMob: 19 (1/m)\nExp: 2,788 (269/m)\nMeso: 2,061 (198/m)\nElapsed: 10:21");
        Assert.True(parsed.FrameGood);
        Assert.Equal(19, parsed.Kills);
        Assert.Equal(56511, parsed.Damage);
        Assert.Equal(2788, parsed.Exp);
        Assert.Equal(2061, parsed.Meso);

        var header = BattleText.Parse("Dmg:\nMob:\nExp:\nMeso:\n56,511 (90/s)\n19 (1/m)\n2,788 (269/m)\n2,061 (198/m)");
        Assert.True(header.FrameGood);
        Assert.Equal(19, header.Kills);

        var damage = BattleText.Parse("Dmg: 56,511 (90/s)\nMob: 56,511 (90/s)\nExp: 2,788 (269/m)\nMeso: 2,061 (198/m)");
        Assert.False(damage.FrameGood);
        Assert.Null(damage.Kills);
    }

    private static byte[] ScaleNearest(byte[] src, int width, int height, int factor)
    {
        var destWidth = width * factor;
        var destHeight = height * factor;
        var dest = new byte[destWidth * destHeight * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = (y * width + x) * 4;
                for (var dy = 0; dy < factor; dy++)
                {
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var to = (((y * factor) + dy) * destWidth + (x * factor) + dx) * 4;
                        dest[to] = src[from];
                        dest[to + 1] = src[from + 1];
                        dest[to + 2] = src[from + 2];
                        dest[to + 3] = src[from + 3];
                    }
                }
            }
        }

        return dest;
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

    private static void Set(byte[] pixels, int width, int x, int y, byte r, byte g, byte b)
    {
        var i = (y * width + x) * 4;
        pixels[i] = b;
        pixels[i + 1] = g;
        pixels[i + 2] = r;
        pixels[i + 3] = 255;
    }
}
