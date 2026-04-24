namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Tracks event positions for pipelined processing where events complete out of order.
/// The safe checkpoint is the highest position where all events at or below it have
/// been processed (or skipped). Used by <see cref="ControlLoop"/> in pipelined mode.
/// </summary>
internal sealed class PositionWatermark
{
    private long _readPosition;
    private readonly SortedSet<long> _inFlight = new();
    private readonly Lock _lock = new();

    public PositionWatermark(long initialPosition)
    {
        _readPosition = initialPosition;
    }

    /// <summary>
    /// The highest position that has been read from the event store.
    /// </summary>
    public long ReadPosition
    {
        get { lock (_lock) return _readPosition; }
    }

    /// <summary>
    /// Advances the read position to the given value if it's higher than the current.
    /// Called for every event read from the stream (matching or not).
    /// </summary>
    public void AdvanceReadPosition(long position)
    {
        lock (_lock)
        {
            if (position > _readPosition)
                _readPosition = position;
        }
    }

    /// <summary>
    /// Marks an event as dispatched to a worker. Must be called before the worker starts.
    /// </summary>
    public void MarkDispatched(long position)
    {
        lock (_lock) _inFlight.Add(position);
    }

    /// <summary>
    /// Marks a dispatched event as completed. Called by the worker after processing.
    /// </summary>
    public void MarkCompleted(long position)
    {
        lock (_lock) _inFlight.Remove(position);
    }

    /// <summary>
    /// The highest position that is safe to checkpoint. All events at or below this
    /// position have been processed or skipped.
    /// </summary>
    public long SafeCheckpoint
    {
        get
        {
            lock (_lock)
            {
                if (_inFlight.Count == 0)
                    return _readPosition;

                // Can only checkpoint up to just before the earliest in-flight event
                return _inFlight.Min - 1;
            }
        }
    }

    /// <summary>
    /// Number of events currently being processed by workers.
    /// </summary>
    public int InFlightCount
    {
        get { lock (_lock) return _inFlight.Count; }
    }
}
