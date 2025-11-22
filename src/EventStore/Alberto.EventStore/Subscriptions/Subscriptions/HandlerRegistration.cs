using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Subscriptions;

/// <summary>
/// Represents a registered event handler with its checkpoint and supported event types
/// </summary>
public sealed class HandlerRegistration
{
    public required string SubscriptionId { get; init; }
    public required Type HandlerType { get; init; }
    public required HashSet<string> SupportedEventTypes { get; init; }
    public required ILogger Logger { get; init; }

    /// <summary>
    /// Current checkpoint position for this handler
    /// </summary>
    public long Position { get; set; } = -1;

    /// <summary>
    /// Events processed since last checkpoint save
    /// </summary>
    public int EventsProcessedSinceCheckpoint { get; set; } = 0;
}