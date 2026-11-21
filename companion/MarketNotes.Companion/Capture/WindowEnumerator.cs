using System.Diagnostics;
using System.Text;

namespace MarketNotes.Companion.Capture;

public static class WindowEnumerator
{
    public static IReadOnlyList<SharedWindow> TopLevelWindows()
    {
        var found = new List<SharedWindow>();
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            var length = NativeMethods.GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var builder = new StringBuilder(length + 1);
            _ = NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            if (title.Length == 0) return true;
            if (title is "Screen share" or "Market Notes Companion" or "Scanner overlay" or "Program Manager") return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var ownerPid);
            if (ownerPid == (uint)Environment.ProcessId) return true;
            found.Add(new SharedWindow
            {
                Handle = hwnd,
                Title = title,
                ProcessName = ProcessName(hwnd),
            });
            return true;
        };
        _ = NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        found.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
        return found;
    }

    private static string ProcessName(IntPtr hwnd)
    {
        try
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "unknown";
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        }
        catch
        {
            return "unknown";
        }
    }
}
