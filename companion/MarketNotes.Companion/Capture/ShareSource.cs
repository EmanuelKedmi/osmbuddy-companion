namespace MarketNotes.Companion.Capture;

public enum ShareKind
{
    Window,
    Screen,
}

/// <summary>
/// One source the player can share. A window carries its title and process.
/// A screen carries its index and bounds.
/// </summary>
public sealed class ShareSource
{
    public ShareKind Kind { get; init; }
    public IntPtr WindowHandle { get; init; }
    public int ScreenIndex { get; init; }
    public string Title { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public string StatusLabel => Kind == ShareKind.Screen
        ? $"Screen {ScreenIndex + 1} ({Width}×{Height} at {X},{Y})"
        : string.IsNullOrWhiteSpace(ProcessName) || ProcessName == "unknown"
            ? $"window \"{Title}\""
            : $"window \"{Title}\" ({ProcessName})";

    public string Description => Kind == ShareKind.Screen
        ? $"Entire screen. Screen {ScreenIndex + 1}, {Width}×{Height}, origin {X},{Y}."
        : $"Window. {Title}. Process {ProcessName}.";
}

public sealed class SharedWindow
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }

    public string Display => string.IsNullOrWhiteSpace(ProcessName) || ProcessName == "unknown"
        ? Title
        : $"{Title} — {ProcessName}";
}

public sealed class SharedScreen
{
    public required int Index { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    public string Display => $"Screen {Index + 1} — {Width}×{Height} at {X},{Y}";
}
