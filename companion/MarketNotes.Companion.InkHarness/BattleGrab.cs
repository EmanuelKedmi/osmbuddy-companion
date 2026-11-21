using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// Saves the battle analysis window off the live game, twice a second, for the battle bench.
///   --battle-grab 180      seconds to run; crops go to ocr-bench\battle\crops
/// PrintWindow of the game client, so it does not need the share running.
/// </summary>
static class BattleGrab
{
    public static int Run(string seconds)
    {
        var process = Process.GetProcessesByName("msw").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (process == null)
        {
            Console.WriteLine("The game is not running.");
            return 1;
        }

        var hwnd = process.MainWindowHandle;
        SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd));
        var dir = Path.Combine(BattleBench.Root, "crops");
        Directory.CreateDirectory(dir);
        var until = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture));
        string? last = null;
        int saved = 0, missing = 0;
        while (until.Elapsed < limit)
        {
            var tick = Stopwatch.StartNew();
            var client = Client(hwnd, out var width, out var height);
            var window = client == null ? null : BattleWindowFinder.Find(client, width, height, width * 4);
            if (window is { } box)
            {
                var crop = Pixels.Crop(client!, width, box.X, box.Y, box.Width, box.Height);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(crop))[..16];
                if (hash != last)
                {
                    last = hash;
                    Pixels.Save(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png"), crop, box.Width, box.Height);
                    saved++;
                }
            }
            else
            {
                missing++;
            }

            var rest = 500 - (int)tick.ElapsedMilliseconds;
            if (rest > 0) Thread.Sleep(rest);
        }

        Console.WriteLine($"saved {saved} crops, {missing} grabs without the battle window, in {dir}");
        return 0;
    }

    /// <summary>--battle-frames DIR: cuts the battle window out of extracted video frames.</summary>
    public static int FromFrames(string frames)
    {
        var dir = Path.Combine(BattleBench.Root, "crops");
        Directory.CreateDirectory(dir);
        var prefix = "video-" + new DirectoryInfo(frames).Name + "-";
        int saved = 0, missing = 0;
        foreach (var path in Directory.EnumerateFiles(frames, "*.png").OrderBy(path => path, StringComparer.Ordinal))
        {
            var image = Pixels.Load(path);
            if (BattleWindowFinder.Find(image.Bgra, image.Width, image.Height, image.Width * 4) is not { } box)
            {
                missing++;
                continue;
            }

            var crop = Pixels.Crop(image.Bgra, image.Width, box.X, box.Y, box.Width, box.Height);
            Pixels.Save(Path.Combine(dir, prefix + Path.GetFileName(path)), crop, box.Width, box.Height);
            saved++;
        }

        Console.WriteLine($"saved {saved} crops, {missing} frames without the battle window");
        return 0;
    }

    private static byte[]? Client(IntPtr hwnd, out int width, out int height)
    {
        GetWindowRect(hwnd, out var rect);
        GetClientRect(hwnd, out var client);
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        var fullWidth = rect.Right - rect.Left;
        var fullHeight = rect.Bottom - rect.Top;
        width = client.Right - client.Left;
        height = client.Bottom - client.Top;
        if (fullWidth <= 0 || fullHeight <= 0 || width <= 0 || height <= 0) return null;
        using var bitmap = new Bitmap(fullWidth, fullHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            var ok = PrintWindow(hwnd, dc, 2);
            graphics.ReleaseHdc(dc);
            if (!ok) return null;
        }

        var x = origin.X - rect.Left;
        var y = origin.Y - rect.Top;
        var data = bitmap.LockBits(new Rectangle(x, y, width, height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
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
