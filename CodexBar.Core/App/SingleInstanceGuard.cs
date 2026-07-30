namespace CodexBarWindows;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\CodexBarWindows.SingleInstance";
    private const string ActivationEventName = @"Local\CodexBarWindows.ActivationRequested";

    private readonly Mutex mutex;
    private readonly EventWaitHandle activationEvent;
    private readonly RegisteredWaitHandle activationWait;

    // Raised on a thread pool thread whenever another instance launches and exits.
    public event Action? ActivationRequested;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activationEvent)
    {
        this.mutex = mutex;
        this.activationEvent = activationEvent;
        activationWait = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            (_, _) => ActivationRequested?.Invoke(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <param name="scope">
    /// Optional suffix isolating one build of the app from another. Omitting it keeps the
    /// original names, so the shipping WinForms app is unaffected; the in-progress WinUI 3
    /// app passes its own scope so both can run side by side during the rewrite.
    /// </param>
    public static SingleInstanceGuard? TryAcquire(string? scope = null)
    {
        var suffix = Suffix(scope);
        var mutex = new Mutex(initiallyOwned: true, MutexName + suffix, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(
            mutex,
            new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivationEventName + suffix));
    }

    public static void SignalExistingInstance(string? scope = null)
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName + Suffix(scope));
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The running instance owns the mutex but has not created its event yet.
        }
    }

    private static string Suffix(string? scope)
    {
        return string.IsNullOrWhiteSpace(scope) ? string.Empty : "." + scope;
    }

    public void Dispose()
    {
        activationWait.Unregister(null);
        activationEvent.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
