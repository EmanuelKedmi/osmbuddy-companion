using System.Diagnostics;
using System.IO;
using MarketNotes.Companion.Launch;
using Microsoft.Win32;

namespace MarketNotes.Companion;

/// <summary>
/// Registers this exe as the current user's ngm:// handler and starts the
/// real NGM executable with the same argument. The argument is not written,
/// logged, or parsed.
/// </summary>
public static class NgmProtocol
{
    private const string CommandKey = @"Software\Classes\ngm\shell\open\command";
    private const string WowCommandKey = @"Software\WOW6432Node\Classes\ngm\shell\open\command";

    public static void EnsureRegistered()
    {
        try
        {
            var ours = OurExecutable();
            if (ours == null || ours.Contains('"')) return;
            var plan = NgmLaunch.Decide(ReadUserCommand(), MachineCommand(), ReadSavedLauncher(), ours);
            if (plan.LauncherPath == null || !File.Exists(plan.LauncherPath)) return;
            WriteSavedLauncher(plan.LauncherPath);
            if (!plan.RewriteHandler) return;
            WriteHandler(ours);
        }
        catch
        {
            // A locked registry key leaves Play on the previous handler.
        }
    }

    /// <summary>
    /// Starts NGM with the original argument. Returns false when NGM is missing
    /// or the argument is not an ngm launch. The argument is never copied elsewhere.
    /// </summary>
    public static bool TryForward(string argument)
    {
        if (!NgmLaunch.IsLaunchArgument(argument)) return false;
        try
        {
            var ours = OurExecutable();
            var launcher = ReadSavedLauncher();
            if (launcher == null || ours == null) return false;
            if (!File.Exists(launcher) || NgmLaunch.SameExecutable(launcher, ours)) return false;
            var start = new ProcessStartInfo
            {
                FileName = launcher,
                WorkingDirectory = Path.GetDirectoryName(launcher) ?? "",
                UseShellExecute = false,
            };
            start.ArgumentList.Add(argument);
            return Process.Start(start) != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? OurExecutable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path;
    }

    private static string? ReadUserCommand()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        return ReadDefault(hive, CommandKey);
    }

    private static string? MachineCommand()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var command = ReadDefault(hive, CommandKey) ?? ReadDefault(hive, WowCommandKey);
        if (NgmLaunch.IsNgmExecutable(NgmLaunch.ExecutableFromCommand(command))) return command;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var name in new[] { "NGM64.exe", "NGM.exe" })
        {
            var path = Path.Combine(root, "Nexon", "NGM", name);
            if (File.Exists(path)) return $"\"{path}\" \"%1\"";
        }

        return command;
    }

    private static string? ReadDefault(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }

    private static void WriteHandler(string ourExecutable)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var scheme = hive.CreateSubKey(@"Software\Classes\ngm");
        scheme.SetValue(null, "URL:NGM Protocol");
        scheme.SetValue("URL Protocol", "");
        using var command = scheme.CreateSubKey(@"shell\open\command");
        command.SetValue(null, $"\"{ourExecutable}\" \"%1\"");
    }

    private static string? ReadSavedLauncher()
    {
        try
        {
            var path = SavedPath();
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return NgmLaunch.IsNgmExecutable(text) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteSavedLauncher(string launcher)
    {
        if (!NgmLaunch.IsNgmExecutable(launcher)) return;
        var path = SavedPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var current = ReadSavedLauncher();
        if (string.Equals(current, launcher, StringComparison.OrdinalIgnoreCase)) return;
        File.WriteAllText(path, launcher);
    }

    private static string SavedPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "MarketNotes.Companion", "ngm-launcher.txt");
    }
}
