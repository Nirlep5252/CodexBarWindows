using System;
using System.Threading;
using CodexBarWindows;
using Microsoft.UI.Dispatching;

namespace CodexBar.WinUI;

public static class Program
{
    /// <summary>
    /// Scope suffix for <see cref="SingleInstanceGuard"/>. While the WinUI 3 rewrite runs
    /// side by side with the shipping WinForms app they must NOT share a mutex name, or
    /// launching one would silently exit because the other already owns it. At cutover this
    /// becomes <c>null</c> so the WinUI app inherits the original single-instance identity.
    /// </summary>
    internal const string SingleInstanceScope = "WinUI";

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
