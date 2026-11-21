namespace MarketNotes.Companion.Launch;

/// <summary>
/// Decides how the companion sits in front of Nexon Game Manager.
/// The ngm:// argument is a login handoff. Callers may pass that string
/// through to NGM64.exe. They must not store it, log it, or take it apart.
/// </summary>
public static class NgmLaunch
{
    public const int MaxArgumentLength = 16 * 1024;

    public static bool IsLaunchArgument(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return false;
        if (argument.Length > MaxArgumentLength) return false;
        var start = 0;
        while (start < argument.Length && char.IsWhiteSpace(argument[start])) start++;
        if (argument.Length - start < 4) return false;
        return argument.AsSpan(start, 4).Equals("ngm:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The program a protocol command runs. Arguments after the executable,
    /// including the launch string, are ignored.
    /// </summary>
    public static string? ExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text.Length >= 2 && text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end <= 1) return null;
            return text[1..end];
        }

        var cut = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is ' ' or '\t')
            {
                cut = i;
                break;
            }
        }

        return cut == 0 ? null : text[..cut];
    }

    public static bool IsNgmExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path.Trim().Trim('"'));
        return name.Equals("NGM64.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NGM.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameExecutable(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a == null || b == null) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// userCommand is the current-user protocol command. machineCommand is the
    /// machine command, or a quoted path when the exe was found on disk.
    /// A saved path is only a fallback for the real NGM executable.
    /// </summary>
    public static NgmRegistration Decide(
        string? userCommand,
        string? machineCommand,
        string? savedLauncher,
        string ourExecutable)
    {
        var userExe = ExecutableFromCommand(userCommand);
        var weOwnUser = SameExecutable(userExe, ourExecutable);
        var launcher = RealLauncher(weOwnUser ? null : userExe, ourExecutable)
            ?? RealLauncher(ExecutableFromCommand(machineCommand), ourExecutable)
            ?? RealLauncher(savedLauncher, ourExecutable);
        return new NgmRegistration(launcher, launcher != null && !weOwnUser);
    }

    private static string? RealLauncher(string? path, string ourExecutable)
    {
        if (!IsNgmExecutable(path)) return null;
        var trimmed = path!.Trim().Trim('"');
        if (SameExecutable(trimmed, ourExecutable)) return null;
        return trimmed;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0) return null;
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
        catch (NotSupportedException)
        {
            return trimmed;
        }
    }
}

public sealed record NgmRegistration(string? LauncherPath, bool RewriteHandler);
