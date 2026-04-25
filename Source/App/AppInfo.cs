using System.Reflection;

namespace CodexBarWindows;

public static class AppInfo
{
    public const string AppName = "CodexBarWindows";

    public static string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            return string.IsNullOrWhiteSpace(version)
                ? CurrentVersion.ToString()
                : version;
        }
    }

    public static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null
                ? new Version(0, 0, 0)
                : new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }
}
