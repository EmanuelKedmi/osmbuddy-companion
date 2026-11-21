using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MarketNotes.Companion.Capture;

namespace MarketNotes.Companion.Overlay;

/// <summary>
/// A click-through window parked on the shared game window.
/// It draws the regions the scanner is watching and the latest seen state.
/// </summary>
public partial class GameOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private bool _debug;
    private IReadOnlyList<SeenRegion> _regions = [];

    public GameOverlayWindow()
    {
        InitializeComponent();
    }

    public void SetDebug(bool show)
    {
        _debug = show;
        if (SeenHost != null) SeenHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DrawRegions();
    }

    public void Place(ShareSource source)
    {
        if (!TryBounds(source, out var x, out var y, out var width, out var height))
        {
            Hide();
            return;
        }

        if (!IsVisible) Show();
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        Left = x / scaleX;
        Top = y / scaleY;
        Width = Math.Max(1, width / scaleX);
        Height = Math.Max(1, height / scaleY);
        ParkSeenHost();
    }

    /// <summary>
    /// The card sits under the minimap and above the loot band and the
    /// character name. A centered card on a short client covers the title.
    /// </summary>
    private void ParkSeenHost()
    {
        if (SeenHost == null || Height <= 1) return;
        var top = Height * 0.40;
        SeenHost.VerticalAlignment = VerticalAlignment.Top;
        SeenHost.Margin = new Thickness(12, top, 0, 0);
        SeenHost.MaxHeight = Math.Max(80, Height * 0.90 - top);
    }

    public void ShowSeen(string text, IReadOnlyList<SeenRegion> regions)
    {
        _regions = regions;
        if (SeenText != null) SeenText.Text = text;
        DrawRegions();
    }

    private void DrawRegions()
    {
        if (RegionCanvas == null) return;
        RegionCanvas.Children.Clear();
        if (!_debug || Width <= 1 || Height <= 1) return;
        foreach (var region in _regions)
        {
            var box = new Rectangle
            {
                Width = Math.Max(1, region.Width * Width),
                Height = Math.Max(1, region.Height * Height),
                Stroke = new SolidColorBrush(Color.FromRgb(0x9E, 0x34, 0x2C)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x9E, 0x34, 0x2C)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, region.X * Width);
            Canvas.SetTop(box, region.Y * Height);
            RegionCanvas.Children.Add(box);
            var label = new TextBlock
            {
                Text = region.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x24, 0x1C)),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xF7, 0xF1, 0xE4)),
                FontSize = 12,
                Padding = new Thickness(4, 1, 4, 1),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, region.X * Width + 4);
            Canvas.SetTop(label, region.Y * Height + 4);
            RegionCanvas.Children.Add(label);
        }
    }

    public void CloseOverlay()
    {
        if (IsVisible) Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    private static bool TryBounds(ShareSource source, out int x, out int y, out int width, out int height)
    {
        if (source.Kind == ShareKind.Screen)
        {
            x = source.X;
            y = source.Y;
            width = source.Width;
            height = source.Height;
            return width > 0 && height > 0;
        }

        if (NativeMethods.IsWindow(source.WindowHandle) && TryClient(source.WindowHandle, out x, out y, out width, out height))
        {
            return true;
        }

        x = y = width = height = 0;
        return false;
    }

    /// <summary>
    /// The grab is the client, after the title bar and border are removed.
    /// Region fractions belong on that rectangle. Parking the overlay on
    /// the whole window draws every box up by the caption.
    /// </summary>
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}

public readonly record struct SeenRegion(string Name, double X, double Y, double Width, double Height);
