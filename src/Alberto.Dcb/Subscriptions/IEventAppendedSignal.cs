namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// A lightweight, coalescing wake-up signal raised when new events may have been
/// appended. Backends that can push a notification (e.g. PostgreSQL LISTEN/NOTIFY)
/// call <see cref="Signal"/>; <see cref="EventStoreHead"/> awaits
/// <see cref="WaitAsync"/> so it can refresh immediately instead of waiting out its
/// polling interval.
///
/// Implementations must be safe for a single waiter and multiple concurrent
/// signallers, and must coalesce: a <see cref="Signal"/> raised while no one is
/// waiting is remembered and satisfies the next <see cref="WaitAsync"/>.
/// </summary>
public interface IEventAppendedSignal
{
    /// <summary>Raises the signal, waking a current or the next waiter.</summary>
    void Signal();

    /// <summary>
    /// Completes when the signal is (or has already been) raised, or when
    /// <paramref name="timeout"/> elapses (whichever comes first). The timeout keeps
    /// the caller's polling interval as an upper bound so it still makes progress if
    /// a signal is missed. Throws <see cref="OperationCanceledException"/> if
    /// <paramref name="cancellationToken"/> is cancelled. Auto-resets on completion.
    /// </summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
