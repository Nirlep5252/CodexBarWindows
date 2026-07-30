namespace CodexBar.WinUI;

/// <summary>
/// Every name by which this shell is distinct from the shipping WinForms app, in one place.
/// </summary>
/// <remarks>
/// <para>
/// The WinUI 3 rewrite runs SIDE BY SIDE with the WinForms app until the user cuts over
/// deliberately. Side by side means both can be installed, both can autostart, and both can run
/// at the same time - so every OS-level name either app claims has to be distinct, or installing
/// or configuring one silently breaks the other:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="SingleInstanceMutexScope"/> - a shared mutex name would make launching one app exit
/// immediately because the other already owned it.
/// </description></item>
/// <item><description>
/// <see cref="StartupRegistryValueName"/> - a shared HKCU\...\Run value would mean the shell
/// toggled last replaced the other's autostart entry, and turning autostart off in one deleted
/// the other's.
/// </description></item>
/// <item><description>
/// <see cref="InstallFolderName"/> - a shared install folder would mean each MSI's uninstall
/// removed the other app's files. It is also what
/// <see cref="CodexBarWindows.GitHubReleaseUpdater.IsInstalledBuild"/> tests, so it has to match
/// what Installer/PackageWinUI.wxs writes or the self-updater goes permanently quiet.
/// </description></item>
/// <item><description>
/// <see cref="ReleaseAssetNameHint"/> - a release that carries an MSI per shell needs each app to
/// download ITS OWN. The WinForms shell filters on "CodexBarWindows", so this must not contain
/// that string; that is why the MSI is CodexBar.WinUI-*.msi and not CodexBarWindows.WinUI-*.msi.
/// </description></item>
/// </list>
/// <para>
/// AT CUTOVER: set all four back to the WinForms values - scope <c>null</c>,
/// <c>CodexBarWindows</c> for the other three - and the WinUI app takes over the installed app's
/// identity, autostart entry and update feed wholesale. Nothing else in the shell hard-codes them.
/// </para>
/// </remarks>
internal static class ShellIdentity
{
    /// <summary>Suffix for <see cref="CodexBarWindows.SingleInstanceGuard"/>'s mutex/event names.</summary>
    internal const string SingleInstanceMutexScope = "WinUI";

    /// <summary>Value name under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
    internal const string StartupRegistryValueName = "CodexBar.WinUI";

    /// <summary>Folder under %LOCALAPPDATA%\Programs that the MSI installs into.</summary>
    internal const string InstallFolderName = "CodexBar.WinUI";

    /// <summary>Substring the GitHub release MSI asset must contain.</summary>
    internal const string ReleaseAssetNameHint = "CodexBar.WinUI";

    /// <summary>The updater configured for this shell.</summary>
    internal static CodexBarWindows.GitHubReleaseUpdater CreateUpdater() =>
        new(InstallFolderName, ReleaseAssetNameHint);
}
