namespace CodexBarWindows;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var instanceGuard = SingleInstanceGuard.TryAcquire();
        if (instanceGuard is null)
        {
            SingleInstanceGuard.SignalExistingInstance();
            return;
        }

        ApplicationConfiguration.Initialize();
        // Prime FluentTheme.VibesActive before any UI (including the tray icon) is created.
        _ = UiSettings.Load();
        using var context = new TrayApplicationContext();
        instanceGuard.ActivationRequested += context.NotifyAlreadyRunning;
        Application.Run(context);
    }
}
