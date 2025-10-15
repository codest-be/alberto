namespace Alberto.EventStore.Subscriptions.Channel;

/// <summary>
/// Generic consumer interface for channel-based event notifications.
/// Implement this to create custom integrations like SignalR broadcasting,
/// WebSocket notifications, audit logging, or metrics collection.
/// </summary>
public interface IChannelConsumer
{
    /// <summary>
    /// Consumes an event from the channel
    /// </summary>
    /// <param name="event">The event envelope with tenant information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task Consume(GlobalEventEnvelope @event, CancellationToken cancellationToken = default);
}