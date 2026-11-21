using System.Reflection;

namespace MarketNotes.Companion;

static class RunningVersion
{
    public static string Label()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return AppVersion.Format(informational, assembly.GetName().Version);
    }
}
