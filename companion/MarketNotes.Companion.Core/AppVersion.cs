namespace MarketNotes.Companion;

/// <summary>
/// The release number on the companion window: major.minor.patch.
/// Build metadata after + is not part of the release.
/// </summary>
public static class AppVersion
{
    public static string Format(string? informational, Version? assembly)
    {
        var release = Release(informational);
        if (release != null) return release;
        if (assembly == null) return "0.0.0";
        var patch = assembly.Build < 0 ? 0 : assembly.Build;
        return $"{assembly.Major}.{assembly.Minor}.{patch}";
    }

    public static string? Release(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return null;
        var text = informational.Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        var dash = text.IndexOf('-');
        var core = dash >= 0 ? text[..dash] : text;
        if (!IsCore(core)) return null;
        if (dash < 0) return core;
        var pre = text[(dash + 1)..];
        return pre.Length == 0 ? core : core + "-" + pre;
    }

    private static bool IsCore(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        foreach (var part in parts)
        {
            if (part.Length == 0) return false;
            foreach (var c in part)
            {
                if (!char.IsDigit(c)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the site release is a newer major.minor.patch than this build.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var next) || !TryParse(current, out var have)) return false;
        if (next.Major != have.Major) return next.Major > have.Major;
        if (next.Minor != have.Minor) return next.Minor > have.Minor;
        return next.Patch > have.Patch;
    }

    public static bool TryParse(string? text, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        var core = Release(text);
        if (core == null) return false;
        var dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch))
        {
            return false;
        }

        if (major < 0 || minor < 0 || patch < 0) return false;
        version = (major, minor, patch);
        return true;
    }
}
