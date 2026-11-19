namespace Alberto;

/// <summary>
/// Ends an idle wait early. A notify that arrives while nobody is waiting is kept, so the next
/// wait returns at once; several notifies before that wait collapse into one.
/// </summary>
internal sealed class WakeSignal
{
    // A semaphore rather than WhenAny(Task.Delay, ...): a timed-out WaitAsync leaves no orphaned
    // waiter behind to swallow the next notify.
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount != 0)
            return;
        try { _signal.Release(); }
        catch (SemaphoreFullException) { } // a concurrent notify got there first
    }

    /// <summary>Waits until notified.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);

    /// <summary>Waits until notified or <paramref name="timeout"/> elapses.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}
