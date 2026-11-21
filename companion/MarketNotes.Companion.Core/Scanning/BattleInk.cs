namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Battle Analysis text is dark on a pale panel. The map behind a translucent
/// window is green and brown, and Windows OCR reads that texture as letters.
/// Keep the dark glyphs and paint everything else white, the opposite of the ink.
/// </summary>
public static class BattleInk
{
    public static bool IsInk(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        // The panel is pale and the leaves behind the numbers are bright.
        // Glyphs stay dark, including where the font sits on a leaf.
        if (max > 96) return false;
        // Squirrels and bark are warm. The battle font is cool, or green only
        // because a leaf shows through the dark stroke.
        if (r > g && r > b + 8 && max > 40) return false;
        return true;
    }

    public static byte[] Isolate(byte[] bgra, int width, int height)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0 || bgra.Length != width * height * 4)
        {
            return bgra;
        }

        var ink = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            ink[i] = 255;
            ink[i + 1] = 255;
            ink[i + 2] = 255;
            ink[i + 3] = 255;
            if (!IsInk(bgra[i + 2], bgra[i + 1], bgra[i])) continue;
            ink[i] = 0;
            ink[i + 1] = 0;
            ink[i + 2] = 0;
        }

        return ink;
    }
}
