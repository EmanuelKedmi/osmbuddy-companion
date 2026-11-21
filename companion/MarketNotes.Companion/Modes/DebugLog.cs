namespace MarketNotes.Companion.Modes;

/// <summary>
/// Loot, battle, and visit logs are written while the debug panel is open.
/// A dev build writes them all the time; the deployed build does not.
/// </summary>
public static class DebugLog
{
#if DEV_BUILD
    public static readonly bool DevBuild = true;
#else
    public static readonly bool DevBuild = false;
#endif

    /// <summary>The debug panel is open.</summary>
    public static bool Panel { get; set; }

    /// <summary>Logs and ink samples are being written.</summary>
    public static bool Enabled => DevBuild || Panel;
}
