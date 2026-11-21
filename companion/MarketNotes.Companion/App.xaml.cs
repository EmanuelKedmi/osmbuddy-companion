using System.Windows;
using MarketNotes.Companion.Launch;

namespace MarketNotes.Companion;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var launch = LaunchArgument(e.Args);
        if (NgmHijackPreference.IsEnabled())
            NgmProtocol.EnsureRegistered();
        else
            NgmProtocol.Uninstall();
        var primary = SingleInstance.TryBecomePrimary();
        var handedToNgm = launch != null && NgmProtocol.TryForward(launch);
        if (!primary)
        {
            SingleInstance.SignalActivate();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (launch != null) window.OnNgmLaunch(handedToNgm);
        SingleInstance.Listen(() => window.Dispatcher.BeginInvoke(window.OnNgmActivate));
        Exit += (_, _) => SingleInstance.Stop();
    }

    private static string? LaunchArgument(string[] args)
    {
        foreach (var arg in args)
        {
            if (NgmLaunch.IsLaunchArgument(arg)) return arg;
        }

        return null;
    }
}
