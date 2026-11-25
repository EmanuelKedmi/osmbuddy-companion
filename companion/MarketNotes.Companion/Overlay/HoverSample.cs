using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MarketNotes.Companion.Capture;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// A screen copy of one client rectangle. The share loop grabs the whole
/// window about twice a second, which is too slow to open a tooltip. This
/// copies only the pixels around the pointer.
/// </summary>
static class HoverSample
{
    public readonly record struct Shot(CapturedFrame Frame, int OriginX, int OriginY);

    public static Shot? Copy(ShareSource source, ClientPixels client, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var left = Math.Clamp(x, 0, Math.Max(0, client.Width - 1));
        var top = Math.Clamp(y, 0, Math.Max(0, client.Height - 1));
        var right = Math.Clamp(x + width, left + 1, client.Width);
        var bottom = Math.Clamp(y + height, top + 1, client.Height);
        var w = right - left;
        var h = bottom - top;
        if (w < 8 || h < 8 || w > 2000 || h > 2000) return null;

        var previous = IntPtr.Zero;
        var dpi = source.Kind == ShareKind.Window && NativeMethods.IsWindow(source.WindowHandle);
        if (dpi) previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.GetWindowDpiAwarenessContext(source.WindowHandle));
        try
        {
            using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(client.X + left, client.Y + top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }

            return new Shot(Pack(bitmap), left, top);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException)
        {
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(previous);
        }
    }

    private static CapturedFrame Pack(Bitmap bitmap)
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

            return new CapturedFrame { Width = bitmap.Width, Height = bitmap.Height, Bgra = packed };
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
