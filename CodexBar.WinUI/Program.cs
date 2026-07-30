using System;
using System.Threading;
using CodexBarWindows;
using Microsoft.UI.Dispatching;

namespace CodexBar.WinUI;

public static class Program
{
    /// <summary>
    /// Scope suffix for <see cref="SingleInstanceGuard"/>. See <see cref="ShellIdentity"/>, which
    /// holds every side-by-side name and the cutover instructions for all of them.
    /// </summary>
    internal const string SingleInstanceScope = ShellIdentity.SingleInstanceMutexScope;

    private static SingleInstanceGuard? singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        singleInstance = SingleInstanceGuard.TryAcquire(SingleInstanceScope);
        if (singleInstance is null)
        {
            // Hand over to the running instance, which pops its flyout, then quit.
            SingleInstanceGuard.SignalExistingInstance(SingleInstanceScope);
            return 0;
        }

        DiagnosticLog.Write("process start pid={0}", Environment.ProcessId);

        WinRT.ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start(startupParams =>
        {
            // GetForCurrentThread, NOT GetCurrentThread: the latter does not exist here.
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(singleInstance);
        });

        singleInstance.Dispose();
        singleInstance = null;
        DiagnosticLog.Write("process exit");
        return 0;
    }
}
