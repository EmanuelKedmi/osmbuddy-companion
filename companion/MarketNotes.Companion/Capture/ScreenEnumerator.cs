using System.Runtime.InteropServices;

namespace MarketNotes.Companion.Capture;

public static class ScreenEnumerator
{
    public static IReadOnlyList<SharedScreen> Screens()
    {
        var found = new List<SharedScreen>();
        NativeMethods.MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MonitorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return true;
            var width = info.Monitor.Right - info.Monitor.Left;
            var height = info.Monitor.Bottom - info.Monitor.Top;
            if (width <= 0 || height <= 0) return true;
            found.Add(new SharedScreen
            {
                Index = found.Count,
                X = info.Monitor.Left,
                Y = info.Monitor.Top,
                Width = width,
                Height = height,
            });
            return true;
        };
        _ = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }
}
