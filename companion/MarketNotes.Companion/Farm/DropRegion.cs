using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// Pickup lines are the white "You have gained" text in the bottom-right,
/// above the skill bar. The lower-left of the client is the chat.
/// </summary>
public static class DropRegion
{
    public const double LeftFraction = LootRegion.Left;
    public const double TopFraction = LootRegion.Top;
    public const double RightFraction = LootRegion.Right;
    public const double BottomFraction = LootRegion.Bottom;

    public static byte[] Crop(CapturedFrame frame) => CropBitmap(frame).Bgra;

    public static PixelRect Box(CapturedFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length == 0 || frame.Height == 0 || frame.Bgra.Length % frame.Height != 0)
        {
            return LootRegion.Bounds(Math.Max(1, frame.Width), Math.Max(1, frame.Height));
        }

        var stride = frame.Bgra.Length / frame.Height;
        return LootRegion.ForFrame(frame.Bgra, frame.Width, frame.Height, stride);
    }

    public static RegionBitmap CropBitmap(CapturedFrame frame)
    {
        var box = Box(frame);
        return RegionCrop.CropRect(frame, box.X, box.Y, box.Width, box.Height);
    }

    public static string Hash(ReadOnlySpan<byte> pixels)
    {
        ulong hash = 14695981039346656037UL;
        for (var i = 0; i < pixels.Length; i++)
        {
            hash ^= pixels[i];
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16");
    }
}
