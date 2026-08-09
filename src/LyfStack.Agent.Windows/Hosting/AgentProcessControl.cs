namespace LyfStack.Agent.Windows.Hosting;

/// <summary>
/// Single-instance guard + cooperative stop signal for headless/background runs.
/// </summary>
public sealed class AgentProcessControl : IDisposable
{
    public const string MutexName = @"Local\LyfStack.Agent.Windows";
    public const string StopEventName = @"Local\LyfStack.Agent.Windows.Stop";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _stopEvent;
    private readonly bool _ownsMutex;

    private AgentProcessControl(Mutex mutex, bool ownsMutex, EventWaitHandle stopEvent)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _stopEvent = stopEvent;
    }

    public WaitHandle StopHandle => _stopEvent;

    public static bool TryAcquire(out AgentProcessControl? control)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            control = null;
            return false;
        }

        var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);
        control = new AgentProcessControl(mutex, ownsMutex: true, stopEvent);
        return true;
    }

    public static bool RequestStop()
    {
        try
        {
            using var stopEvent = EventWaitHandle.OpenExisting(StopEventName);
            return stopEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stopEvent.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released.
            }
        }

        _mutex.Dispose();
    }
}
