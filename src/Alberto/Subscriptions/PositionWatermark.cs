namespace Alberto.Subscriptions;

/// <summary>
/// Tracks event positions for pipelined processing where events complete out of order.
/// The safe checkpoint is the highest position where all events at or below it have
/// been processed (or skipped). Used by <see cref="ControlLoop"/> in pipelined mode.
/// </summary>
internal sealed class PositionWatermark
{
    private long _readPosition;
    // Kept in ascending sorted order for O(1) minimum lookup.
    // MaxConcurrency is typically small (4–32) so List<long> avoids
    // the per-element tree-node allocation of SortedSet<long>.
    private readonly List<long> _inFlight = new();
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
        lock (_lock)
        {
            var idx = _inFlight.BinarySearch(position);
            // idx < 0 means not found; ~idx is the insertion point
            if (idx < 0) _inFlight.Insert(~idx, position);
        }
    }

    /// <summary>
    /// Marks a dispatched event as completed. Called by the worker after processing.
    /// Only call this when dispatch actually finished (success or handled failure) — do
    /// NOT call it when processing was cancelled so the safe checkpoint stays behind the
    /// in-flight position.
    /// </summary>
    public void MarkCompleted(long position)
    {
        lock (_lock)
        {
            var idx = _inFlight.BinarySearch(position);
            if (idx >= 0) _inFlight.RemoveAt(idx);
        }
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

                // _inFlight[0] is the minimum because the list is kept sorted
                return _inFlight[0] - 1;
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
