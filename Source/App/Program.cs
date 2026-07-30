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
        // FluentTheme seeds VibesActive in its own static constructor, so no priming is needed
        // here — touching it below (via the tray icon) resolves it correctly.
        using var context = new TrayApplicationContext();
        instanceGuard.ActivationRequested += context.NotifyAlreadyRunning;
        Application.Run(context);
    }
}
