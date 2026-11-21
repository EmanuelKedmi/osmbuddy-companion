namespace MarketNotes.Companion.Capture;

/// <summary>
/// Crops one rectangle out of a grabbed window. Fractions are relative to the frame.
/// </summary>
public readonly record struct RegionBitmap(byte[] Bgra, int Width, int Height)
{
    public bool Ok => Bgra.Length > 0 && Width > 0 && Height > 0 && Bgra.Length == Width * Height * 4;
}

public static class RegionCrop
{
    public static byte[] Crop(CapturedFrame frame, double left, double top, double right, double bottom)
    {
        return CropBitmap(frame, left, top, right, bottom).Bgra;
    }

    public static RegionBitmap CropBitmap(CapturedFrame frame, double left, double top, double right, double bottom)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length == 0) return new RegionBitmap([], 0, 0);
        if (frame.Bgra.Length % frame.Height != 0) return new RegionBitmap([], 0, 0);
        var stride = frame.Bgra.Length / frame.Height;
        if (stride < frame.Width * 4) return new RegionBitmap([], 0, 0);

        var x0 = Clamp((int)(frame.Width * left), 0, Math.Max(0, frame.Width - 1));
        var y0 = Clamp((int)(frame.Height * top), 0, Math.Max(0, frame.Height - 1));
        var x1 = Clamp((int)Math.Ceiling(frame.Width * right), x0 + 1, frame.Width);
        var y1 = Clamp((int)Math.Ceiling(frame.Height * bottom), y0 + 1, frame.Height);
        var width = Math.Max(1, x1 - x0);
        var height = Math.Max(1, y1 - y0);
        if ((y0 + height - 1) * stride + (x0 + width) * 4 > frame.Bgra.Length) return new RegionBitmap([], 0, 0);

        var crop = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(frame.Bgra, (y0 + y) * stride + x0 * 4, crop, y * width * 4, width * 4);
        }

        return new RegionBitmap(crop, width, height);
    }

    public static RegionBitmap CropRect(CapturedFrame frame, int x, int y, int width, int height)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length == 0 || width <= 0 || height <= 0)
        {
            return new RegionBitmap([], 0, 0);
        }

        if (frame.Bgra.Length % frame.Height != 0) return new RegionBitmap([], 0, 0);
        var stride = frame.Bgra.Length / frame.Height;
        if (stride < frame.Width * 4) return new RegionBitmap([], 0, 0);
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x >= frame.Width || y >= frame.Height) return new RegionBitmap([], 0, 0);
        if (x + width > frame.Width) width = frame.Width - x;
        if (y + height > frame.Height) height = frame.Height - y;
        if (width <= 0 || height <= 0) return new RegionBitmap([], 0, 0);

        var crop = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(frame.Bgra, (y + row) * stride + x * 4, crop, row * width * 4, width * 4);
        }

        return new RegionBitmap(crop, width, height);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (max < min) return min;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
