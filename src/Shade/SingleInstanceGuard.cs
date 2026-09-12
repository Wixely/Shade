namespace Shade;

// Acquire and dispose on the application's UI thread. The OS releases ownership
// if the process terminates, so a crashed instance cannot block the next launch.
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;

    private SingleInstanceGuard(Mutex mutex) => this.mutex = mutex;

    public static SingleInstanceGuard? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new(mutex);
            mutex.Dispose();
            return null;
        }
        catch { mutex.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        mutex.ReleaseMutex();
        mutex.Dispose();
        disposed = true;
    }
}
