using MarketNotes.Companion.Capture;

namespace MarketNotes.Companion.Overlay;

public readonly record struct ClientPixels(int X, int Y, int Width, int Height);

/// <summary>
/// The grab is the client, after the title bar and border are removed.
/// Parking a window on the whole frame draws it up by the caption.
/// </summary>
public static class GameClientBounds
{
    public static bool TryGet(ShareSource source, out ClientPixels bounds)
    {
        if (source.Kind == ShareKind.Screen)
        {
            bounds = new ClientPixels(source.X, source.Y, source.Width, source.Height);
            return source.Width > 0 && source.Height > 0;
        }

        if (NativeMethods.IsWindow(source.WindowHandle) && TryClient(source.WindowHandle, out var x, out var y, out var width, out var height))
        {
            bounds = new ClientPixels(x, y, width, height);
            return true;
        }

        bounds = default;
        return false;
    }

    public static bool TryCursor(ShareSource source, ClientPixels client, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!NativeMethods.GetCursorPos(out var point)) return false;
        if (source.Kind == ShareKind.Window && NativeMethods.IsWindow(source.WindowHandle))
        {
            if (!NativeMethods.ScreenToClient(source.WindowHandle, ref point)) return false;
            x = point.X;
            y = point.Y;
        }
        else
        {
            x = point.X - client.X;
            y = point.Y - client.Y;
        }

        return x >= 0 && y >= 0 && x < client.Width && y < client.Height;
    }

    private static bool TryClient(IntPtr hwnd, out int x, out int y, out int width, out int height)
    {
        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.GetWindowDpiAwarenessContext(hwnd));
        try
        {
            if (NativeMethods.GetClientRect(hwnd, out var client))
            {
                var origin = new NativeMethods.Point();
                var clientWidth = client.Right - client.Left;
                var clientHeight = client.Bottom - client.Top;
                if (clientWidth > 0 && clientHeight > 0 && NativeMethods.ClientToScreen(hwnd, ref origin))
                {
                    x = origin.X;
                    y = origin.Y;
                    width = clientWidth;
                    height = clientHeight;
                    return true;
                }
            }

            if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                x = rect.Left;
                y = rect.Top;
                width = rect.Right - rect.Left;
                height = rect.Bottom - rect.Top;
                return width > 0 && height > 0;
            }
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                NativeMethods.SetThreadDpiAwarenessContext(previous);
            }
        }

        x = y = width = height = 0;
        return false;
    }
}
