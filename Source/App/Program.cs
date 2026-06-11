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
        using var context = new TrayApplicationContext();
        instanceGuard.ActivationRequested += context.NotifyAlreadyRunning;
        Application.Run(context);
    }
}
