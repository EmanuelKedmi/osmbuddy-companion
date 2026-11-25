using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MarketNotes.Companion.Overlay;

internal static class OverlayChrome
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    public static void ToolWindow(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExToolWindow | WsExNoActivate));
        };
    }

    public static (double X, double Y) Scale(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var x = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var y = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return (x, y);
    }

    public static void Move(Window window, int screenX, int screenY)
    {
        var (x, y) = Scale(window);
        window.Left = screenX / x;
        window.Top = screenY / y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
