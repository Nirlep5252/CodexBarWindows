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

            if (string.IsNullOrWhiteSpace(version))
            {
                return CurrentVersion.ToString(3);
            }

            var metadataSeparator = version.IndexOf('+', StringComparison.Ordinal);
            return metadataSeparator > 0
                ? version[..metadataSeparator]
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
