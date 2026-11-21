using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Capture;

public sealed class CapturedFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>BGRA pixels, row-major, from the shared source only.</summary>
    public required byte[] Bgra { get; init; }
}

/// <summary>
/// Grabs the source the player chose to share. The companion owns the loop.
/// Windows Graphics Capture is the Windows upgrade for this interface.
/// See <see cref="WindowsGraphicsCapture"/>. This build does not pretend
/// that API returned a frame.
/// </summary>
public interface IScreenCapture
{
    /// <summary>Name of the grab that actually runs in this build.</summary>
    string ImplementationName { get; }

    CapturedFrame? Grab(ShareSource source);
}

public static class ScreenCaptureSelector
{
    public static IScreenCapture Create()
    {
        // WindowsGraphicsCapture.IsAvailable is false here. Do not return a
        // stand-in frame and call it Graphics Capture.
        return new PrintWindowScreenCapture();
    }
}

/// <summary>
/// PrintWindow for a chosen window (flag PW_RENDERFULLCONTENT), measured
/// in that window's own DPI context. The bitmap handed to OCR is the
/// game client, not the title bar and not a DPI-scaled copy of it. If
/// PrintWindow is empty, CopyFromScreen copies the same rectangle.
/// An entire screen is CopyFromScreen of that monitor.
/// </summary>
public sealed class PrintWindowScreenCapture : IScreenCapture
{
    public string ImplementationName => "PrintWindow";

    public CapturedFrame? Grab(ShareSource source)
    {
        if (source.Kind == ShareKind.Screen) return GrabScreen(source);
        return GrabWindow(source.WindowHandle);
    }

    private static CapturedFrame? GrabWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;
        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.GetWindowDpiAwarenessContext(hwnd));
        try
        {
            return GrabWindowCore(hwnd);
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                NativeMethods.SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    private static CapturedFrame? GrabWindowCore(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return null;
        if (!NativeMethods.GetClientRect(hwnd, out var client)) return null;
        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var clientWidth = client.Right - client.Left;
        var clientHeight = client.Bottom - client.Top;
        var clientX = origin.X - rect.Left;
        var clientY = origin.Y - rect.Top;
        if (width <= 0 || height <= 0 || width > 8000 || height > 8000) return null;

        var pixels = PrintDib(hwnd, width, height);
        if (pixels == null)
        {
            pixels = CopyScreen(rect.Left, rect.Top, width, height);
        }

        if (pixels == null) return null;
        var fit = GameFrame.Fit(pixels, width, height, width, height, clientX, clientY, clientWidth, clientHeight);
        if (fit.Width <= 0 || fit.Height <= 0 || fit.Bgra.Length < fit.Width * fit.Height * 4) return null;
        return new CapturedFrame
        {
            Width = fit.Width,
            Height = fit.Height,
            Bgra = fit.Bgra,
        };
    }

    /// <summary>
    /// A device DIB matches the window pixel for pixel. A GDI+ bitmap
    /// defaults to 96 DPI and PrintWindow then scales into it.
    /// </summary>
    private static byte[]? PrintDib(IntPtr hwnd, int width, int height)
    {
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        var mem = NativeMethods.CreateCompatibleDC(screen);
        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                Size = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            },
        };
        var dib = NativeMethods.CreateDIBSection(mem, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(mem);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            return null;
        }

        var old = NativeMethods.SelectObject(mem, dib);
        var printed = false;
        try
        {
            printed = NativeMethods.PrintWindow(hwnd, mem, NativeMethods.PwRenderFullContent);
        }
        finally
        {
            NativeMethods.SelectObject(mem, old);
        }

        byte[]? pixels = null;
        if (printed)
        {
            pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            if (IsMostlyBlank(pixels, width, height)) pixels = null;
        }

        NativeMethods.DeleteObject(dib);
        NativeMethods.DeleteDC(mem);
        NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        return pixels;
    }

    private static byte[]? CopyScreen(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException)
        {
            return null;
        }

        var pixels = PackTight(bitmap);
        return IsMostlyBlank(pixels, width, height) ? null : pixels;
    }

    private static CapturedFrame? GrabScreen(ShareSource source)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Width > 8000 || source.Height > 8000) return null;
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                source.X,
                source.Y,
                0,
                0,
                new Size(source.Width, source.Height),
                CopyPixelOperation.SourceCopy);
        }

        return Pack(bitmap);
    }

    private static bool IsMostlyBlank(byte[] bgra, int width, int height)
    {
        var hits = 0;
        var seen = 0;
        var stepX = Math.Max(1, width / 8);
        var stepY = Math.Max(1, height / 8);
        for (var y = 0; y < height; y += stepY)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x += stepX)
            {
                var i = row + x * 4;
                seen++;
                if (bgra[i] > 8 || bgra[i + 1] > 8 || bgra[i + 2] > 8) hits++;
            }
        }

        return seen == 0 || hits * 10 < seen;
    }

    private static CapturedFrame Pack(Bitmap bitmap)
    {
        return new CapturedFrame
        {
            Width = bitmap.Width,
            Height = bitmap.Height,
            Bgra = PackTight(bitmap),
        };
    }

    private static byte[] PackTight(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var packed = new byte[bitmap.Width * bitmap.Height * 4];
            var rowBytes = bitmap.Width * 4;
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, packed, y * rowBytes, rowBytes);
            }

            return packed;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
