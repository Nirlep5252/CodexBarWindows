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

    public static SingleInstanceGuard? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(
            mutex,
            new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivationEventName));
    }

    public static void SignalExistingInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The running instance owns the mutex but has not created its event yet.
        }
    }

    public void Dispose()
    {
        activationWait.Unregister(null);
        activationEvent.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
