namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Default <see cref="IEventAppendedSignal"/> — an async auto-reset event with
/// signal coalescing. A single waiter (the <see cref="EventStoreHead"/> loop) is
/// woken by any number of signallers; a signal raised between waits is latched so
/// it is not lost. The wait folds in the caller's timeout so there are no abandoned
/// timers or dangling waits.
/// </summary>
public sealed class EventAppendedSignal : IEventAppendedSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _pending;

    /// <inheritdoc />
    public void Signal()
    {
        lock (_gate)
        {
            _pending = true;
            _tcs.TrySetResult();
        }
    }

    /// <inheritdoc />
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_pending)
            {
                Consume();
                return;
            }
            tcs = _tcs;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using (timeoutCts.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), tcs))
        {
            await tcs.Task.ConfigureAwait(false);
        }

        // A timeout is a normal wake (the caller then polls); only a real
        // cancellation propagates.
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Consume();
        }
    }

    // Clears the latched signal and swaps in a fresh, uncompleted source so the
    // next wait blocks until the next Signal. Must be called under _gate.
    private void Consume()
    {
        _pending = false;
        if (_tcs.Task.IsCompleted)
            _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
