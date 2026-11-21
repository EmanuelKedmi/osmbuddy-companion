using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.InkHarness;

/// <summary>
/// One-shot PrintWindow of the live client, OCR every framed UI panel, parse
/// the first Stat window. Writes JSON to stdout and a dump under LocalAppData.
///   --stat-grab
/// </summary>
static class StatGrab
{
    public static int Run()
    {
        if (!WinOcr.Available)
        {
            Console.WriteLine("Windows OCR is not available.");
            return 1;
        }

        var process = Process.GetProcessesByName("msw").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (process == null)
        {
            Console.WriteLine("The game is not running.");
            return 1;
        }

        var hwnd = process.MainWindowHandle;
        SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd));
        var client = Client(hwnd, out var width, out var height);
        if (client == null || width <= 0 || height <= 0)
        {
            Console.WriteLine("The game window could not be grabbed.");
            return 1;
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketNotes.Companion",
            "samples",
            "sheet");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        Pixels.Save(Path.Combine(dir, stamp + "-full.png"), client, width, height);

        // Always try the bottom status plate (LV / HP) even when Stat (S) is closed.
        var statusBox = StatusBarRegion.Bounds(width, height);
        var statusCrop = Pixels.Crop(client, width, statusBox.X, statusBox.Y, statusBox.Width, statusBox.Height);
        Pixels.Save(Path.Combine(dir, stamp + "-status.png"), statusCrop, statusBox.Width, statusBox.Height);
        var statusLines = WinOcr.Read(statusCrop, statusBox.Width, statusBox.Height);
        if (statusLines.Count == 0)
        {
            var scaled = Pixels.Scale(statusCrop, statusBox.Width, statusBox.Height, 2);
            statusLines = WinOcr.Read(scaled, statusBox.Width * 2, statusBox.Height * 2);
        }

        var statusText = string.Join("\n", statusLines);
        File.WriteAllText(Path.Combine(dir, stamp + "-status.txt"), statusText);
        var status = StatusBarText.Parse(statusText);
        Console.WriteLine($"status '{Flat(statusText)}' -> {StatusBarText.Describe(status)}");

        var frames = UiChromeFinder.Frames(client, width, height, width * 4);
        Console.WriteLine($"frames={frames.Count} size={width}x{height}");
        StatSheet? best = null;
        string? bestText = null;
        var index = 0;
        foreach (var rect in frames)
        {
            var crop = Pixels.Crop(client, width, rect.X, rect.Y, rect.Width, rect.Height);
            var lines = WinOcr.Read(crop, rect.Width, rect.Height);
            if (lines.Count == 0)
            {
                var scaled = Pixels.Scale(crop, rect.Width, rect.Height, 2);
                lines = WinOcr.Read(scaled, rect.Width * 2, rect.Height * 2);
            }

            var text = string.Join("\n", lines);
            File.WriteAllText(Path.Combine(dir, $"{stamp}-{index}.txt"), $"rect={rect.X},{rect.Y} {rect.Width}x{rect.Height}\n{text}");
            Pixels.Save(Path.Combine(dir, $"{stamp}-{index}.png"), crop, rect.Width, rect.Height);
            Console.WriteLine($"frame[{index}] {rect.Width}x{rect.Height} lines={lines.Count} '{Flat(text)}'");
            var sheet = StatWindowText.Parse(text);
            if (sheet.Succeeded)
            {
                Console.WriteLine($"  -> STAT {StatWindowText.Describe(sheet, false)}");
                // Prefer a sheet that named the character over a partial crop.
                if (best == null
                    || (sheet.Name != null && best.Name == null)
                    || (sheet.Accuracy != null && best.Accuracy == null)
                    || (sheet.Level != null && best.Level == null))
                {
                    best = sheet;
                    bestText = text;
                }
            }

            index++;
        }

        object payload;
        if (best != null)
        {
            payload = new
            {
                kind = "stat",
                name = best.Name ?? status.Name,
                branch = best.Branch ?? status.Branch,
                level = best.Level ?? status.Level,
                str = best.Str,
                dex = best.Dex,
                @int = best.Int,
                luk = best.Luk,
                maxHp = best.MaxHp ?? status.MaxHp,
                maxMp = best.MaxMp ?? status.MaxMp,
                weaponAttack = best.WeaponAttack,
                magicAttack = best.MagicAttack,
                accuracy = best.Accuracy,
                weaponDef = best.WeaponDef,
                magicDef = best.MagicDef,
                sample = false,
                ocr = true,
            };
            if (bestText != null) File.WriteAllText(Path.Combine(dir, stamp + "-stat.txt"), bestText);
        }
        else if (status.Succeeded)
        {
            Console.WriteLine("No Stat window; using status plate level/HP only. Open Stat (S) for STR/DEX.");
            payload = new
            {
                kind = "stat",
                name = status.Name ?? "MonkeyDKof",
                branch = status.Branch,
                level = status.Level,
                str = (int?)null,
                dex = (int?)null,
                @int = (int?)null,
                luk = (int?)null,
                maxHp = status.MaxHp,
                maxMp = status.MaxMp,
                weaponAttack = (int?)null,
                magicAttack = (int?)null,
                accuracy = (int?)null,
                weaponDef = (int?)null,
                magicDef = (int?)null,
                sample = false,
                ocr = true,
            };
        }
        else
        {
            Console.WriteLine("No readable stat window or status plate. Open Stat (S) and run again.");
            return 2;
        }

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var jsonPath = Path.Combine(dir, stamp + "-stat.json");
        File.WriteAllText(jsonPath, json);
        Console.WriteLine(json);
        Console.WriteLine($"wrote {jsonPath}");
        return 0;
    }

    private static string Flat(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " | ");
        return flat.Length > 200 ? flat[..200] : flat;
    }

    private static byte[]? Client(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!GetClientRect(hwnd, out var client)) return null;
        width = client.Right - client.Left;
        height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0 || width > 8000 || height > 8000) return null;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            },
        };
        var dib = CreateDIBSection(mem, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            return null;
        }

        var old = SelectObject(mem, dib);
        var ok = PrintWindow(hwnd, mem, 2);
        SelectObject(mem, old);
        byte[]? pixels = null;
        if (ok)
        {
            pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
        }

        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return pixels;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public int Colors; }
}
