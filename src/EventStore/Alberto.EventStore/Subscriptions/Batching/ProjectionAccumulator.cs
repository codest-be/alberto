namespace Alberto.EventStore.Subscriptions.Batching;

/// <summary>
/// Accumulates events for a single projection subscription before batch processing.
/// Tracks timing and size thresholds to determine when to flush.
/// </summary>
internal sealed class ProjectionAccumulator
{
    private readonly List<GlobalEventEnvelope> _events = new();
    private DateTime _firstEventTime;

    /// <summary>
    /// Number of events currently accumulated
    /// </summary>
    public int EventCount => _events.Count;

    /// <summary>
    /// Time elapsed since the first event was added
    /// </summary>
    public TimeSpan TimeSinceFirstEvent =>
        _events.Count == 0 ? TimeSpan.Zero : DateTime.UtcNow - _firstEventTime;

    /// <summary>
    /// Add an event to the accumulator
    /// </summary>
    public void Add(GlobalEventEnvelope evt)
    {
        if (_events.Count == 0)
            _firstEventTime = DateTime.UtcNow;

        _events.Add(evt);
    }

    /// <summary>
    /// Get all accumulated events
    /// </summary>
    public IReadOnlyList<GlobalEventEnvelope> GetEvents() => _events;

    /// <summary>
    /// Clear all accumulated events
    /// </summary>
    public void Clear()
    {
        _events.Clear();
        _firstEventTime = default;
    }
}