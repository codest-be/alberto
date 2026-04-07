namespace Alberto.Dcb;

/// <summary>
/// Controls when a reactor executes relative to event persistence.
/// </summary>
public enum ReactorMode
{
    /// <summary>
    /// Reactor runs asynchronously via background polling (ControlLoop).
    /// This is the default mode.
    /// </summary>
    Async,

    /// <summary>
    /// Reactor runs synchronously during <see cref="IEventStore.AppendAsync"/>,
    /// immediately after event persistence and inline projections.
    /// Use for lightweight side effects like sending notifications.
    /// </summary>
    Sync
}
