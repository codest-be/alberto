using Alberto.EventStore.Subscriptions.Batching;
using Alberto.EventStore.Subscriptions.Channel;
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

    /// <summary>
    /// Indicates whether this handler is a projection subscription
    /// </summary>
    public bool IsProjection { get; init; }

    /// <summary>
    /// Subscription mode (Sync, Async, or Hybrid)
    /// </summary>
    public SubscriptionMode SubscriptionMode { get; init; } = SubscriptionMode.Async;

    /// <summary>
    /// Batching configuration for projection subscriptions
    /// </summary>
    public ProjectionBatchingOptions ProjectionBatchingOptions { get; init; } = new();
}