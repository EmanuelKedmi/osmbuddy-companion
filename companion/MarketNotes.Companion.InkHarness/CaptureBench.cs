using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// Times the grabs a fast loot loop could use, on a live window.
///   --capture-bench msw
/// PrintWindow of the whole window (what the share tick does) against a
/// screen copy of only the loot rectangle.
/// </summary>
static class CaptureBench
{
    public static int Run(string processName)
    {
        var process = Process.GetProcessesByName(processName).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (process == null)
        {
            Console.WriteLine("No window for " + processName);
            return 1;
        }

        var hwnd = process.MainWindowHandle;
        SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd));
        GetWindowRect(hwnd, out var rect);
        GetClientRect(hwnd, out var client);
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var cw = client.Right - client.Left;
        var ch = client.Bottom - client.Top;
        var loot = LootRegion.Bounds(cw, ch);
        Console.WriteLine($"window {width}x{height}, client {cw}x{ch}, loot {loot.Width}x{loot.Height}");

        Time("PrintWindow whole window", 30, () => PrintWhole(hwnd, width, height));
        Time("screen copy of loot rect", 120, () => CopyRect(origin.X + loot.X, origin.Y + loot.Y, loot.Width, loot.Height));
        var shot = CopyRect(origin.X + loot.X, origin.Y + loot.Y, loot.Width, loot.Height);
        Pixels.Save(Path.Combine(Path.GetTempPath(), "loot-screen-copy.png"), shot, loot.Width, loot.Height);
        Console.WriteLine(Path.Combine(Path.GetTempPath(), "loot-screen-copy.png"));
        return 0;
    }

    private static void Time(string name, int runs, Func<byte[]> grab)
    {
        grab();
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++) grab();
        clock.Stop();
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        Console.WriteLine($"{name,-28} {clock.Elapsed.TotalMilliseconds / runs,6:0.0} ms  cpu {cpu / runs,6:0.0} ms");
    }

    private static byte[] CopyRect(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[width * height * 4];
            for (var row = 0; row < height; row++) Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * width * 4, width * 4);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static byte[] PrintWhole(IntPtr hwnd, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            PrintWindow(hwnd, dc, 2);
            graphics.ReleaseHdc(dc);
        }

        return [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
