namespace MarketNotes.Companion.Capture;

/// <summary>
/// Windows Graphics Capture (<c>Windows.Graphics.Capture</c>) is the capture
/// API Discord and Teams use. It is the Windows upgrade for
/// <see cref="IScreenCapture"/>: a share keeps receiving frames when another
/// window covers the game.
///
/// This SDK compiles the WPF project as <c>net8.0-windows</c> with
/// EnableWindowsTargeting, and it does not include that WinRT projection.
/// Referencing the API would fail the Linux build, so this type does not
/// call it and never returns a frame. <see cref="PrintWindowScreenCapture"/>
/// is the implementation that compiles. <see cref="IsAvailable"/> is false.
/// Do not treat that as a successful Graphics Capture grab.
/// </summary>
public static class WindowsGraphicsCapture
{
    public static bool IsAvailable => false;
}
