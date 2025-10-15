namespace Alberto.EventStore.Subscriptions.Channel;

/// <summary>
/// Defines how events should be delivered to subscribers
/// </summary>
public enum SubscriptionMode
{
    /// <summary>
    /// Events are delivered immediately via channel after append (synchronous)
    /// </summary>
    Sync,

    /// <summary>
    /// Events are polled from the database at intervals (asynchronous)
    /// </summary>
    Async,

    /// <summary>
    /// Events are delivered via channel when possible, with polling as fallback for catch-up
    /// </summary>
    Hybrid
}