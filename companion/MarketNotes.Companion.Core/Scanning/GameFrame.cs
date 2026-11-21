namespace MarketNotes.Companion.Scanning;

/// <summary>
/// The grab that reaches battle OCR and loot ink has to be the game
/// client's own pixels. A DPI-virtualized or GDI-scaled bitmap is a
/// different size from the window, and PrintWindow includes the title
/// bar and the invisible resize border. Either one shifts the finders
/// and turns a white glyph gray. This puts the client back, with a
/// point sample so a white drop line stays white.
/// </summary>
public static class GameFrame
{
    public static (byte[] Bgra, int Width, int Height) Fit(
        byte[] bgra,
        int width,
        int height,
        int windowWidth,
        int windowHeight,
        int clientX,
        int clientY,
        int clientWidth,
        int clientHeight)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return (bgra, Math.Max(0, width), Math.Max(0, height));
        }

        var pixels = bgra;
        var imageWidth = width;
        var imageHeight = height;
        if (windowWidth > 0 && windowHeight > 0 && (width != windowWidth || height != windowHeight))
        {
            pixels = PointScale(pixels, imageWidth, imageHeight, windowWidth, windowHeight);
            imageWidth = windowWidth;
            imageHeight = windowHeight;
        }

        if (clientWidth > 0 && clientHeight > 0 && imageWidth > 0 && imageHeight > 0)
        {
            var x = Clamp(clientX, 0, imageWidth - 1);
            var y = Clamp(clientY, 0, imageHeight - 1);
            var cropWidth = Math.Min(clientWidth, imageWidth - x);
            var cropHeight = Math.Min(clientHeight, imageHeight - y);
            if (cropWidth > 0 && cropHeight > 0 && (x != 0 || y != 0 || cropWidth != imageWidth || cropHeight != imageHeight))
            {
                pixels = Crop(pixels, imageWidth, x, y, cropWidth, cropHeight);
                imageWidth = cropWidth;
                imageHeight = cropHeight;
            }
        }

        if (TryUnletterbox(pixels, imageWidth, imageHeight, out var inner, out var innerWidth, out var innerHeight))
        {
            return (inner, innerWidth, innerHeight);
        }

        return (pixels, imageWidth, imageHeight);
    }

    /// <summary>
    /// Nearest source pixel. A 2× grab of a white glyph comes back white,
    /// which a filtered scale would turn into gray that loot ink drops.
    /// </summary>
    public static byte[] PointScale(byte[] src, int srcWidth, int srcHeight, int destWidth, int destHeight)
    {
        var dest = new byte[destWidth * destHeight * 4];
        for (var y = 0; y < destHeight; y++)
        {
            var sy = y * srcHeight / destHeight;
            if (sy >= srcHeight) sy = srcHeight - 1;
            for (var x = 0; x < destWidth; x++)
            {
                var sx = x * srcWidth / destWidth;
                if (sx >= srcWidth) sx = srcWidth - 1;
                var from = (sy * srcWidth + sx) * 4;
                var to = (y * destWidth + x) * 4;
                dest[to] = src[from];
                dest[to + 1] = src[from + 1];
                dest[to + 2] = src[from + 2];
                dest[to + 3] = src[from + 3];
            }
        }

        return dest;
    }

    private static bool TryUnletterbox(byte[] pixels, int width, int height, out byte[] cropped, out int cropWidth, out int cropHeight)
    {
        cropped = pixels;
        cropWidth = width;
        cropHeight = height;
        if (width < 64 || height < 64) return false;

        var top = 0;
        var bottom = height - 1;
        var left = 0;
        var right = width - 1;
        while (top < bottom && RowBlack(pixels, width, top)) top++;
        while (bottom > top && RowBlack(pixels, width, bottom)) bottom--;
        while (left < right && ColBlack(pixels, width, left, top, bottom)) left++;
        while (right > left && ColBlack(pixels, width, right, top, bottom)) right--;

        var insetLeft = left;
        var insetRight = width - 1 - right;
        var insetTop = top;
        var insetBottom = height - 1 - bottom;
        var barX = Math.Max(insetLeft, insetRight);
        var barY = Math.Max(insetTop, insetBottom);
        if (barX * 25 < width && barY * 25 < height) return false;

        cropWidth = right - left + 1;
        cropHeight = bottom - top + 1;
        if (cropWidth < 64 || cropHeight < 64) return false;
        if (cropWidth * 5 < width || cropHeight * 5 < height) return false;
        if (cropWidth == width && cropHeight == height) return false;
        cropped = Crop(pixels, width, left, top, cropWidth, cropHeight);
        return true;
    }

    private static bool RowBlack(byte[] pixels, int width, int y)
    {
        var row = y * width * 4;
        for (var x = 0; x < width; x += Math.Max(1, width / 32))
        {
            if (!Black(pixels, row + x * 4)) return false;
        }

        return Black(pixels, row + (width - 1) * 4);
    }

    private static bool ColBlack(byte[] pixels, int width, int x, int top, int bottom)
    {
        var step = Math.Max(1, (bottom - top) / 32);
        for (var y = top; y <= bottom; y += step)
        {
            if (!Black(pixels, (y * width + x) * 4)) return false;
        }

        return Black(pixels, (bottom * width + x) * 4);
    }

    private static bool Black(byte[] pixels, int index)
    {
        var max = Math.Max(pixels[index], Math.Max(pixels[index + 1], pixels[index + 2]));
        return max <= 12;
    }

    private static byte[] Crop(byte[] pixels, int stridePixels, int x, int y, int width, int height)
    {
        var crop = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(pixels, ((y + row) * stridePixels + x) * 4, crop, row * width * 4, width * 4);
        }

        return crop;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
