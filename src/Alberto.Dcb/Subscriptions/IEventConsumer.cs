namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Event consumer that routes events from the store to processors.
/// Consumers are "dumb" routers - they handle delivery, not logic.
/// </summary>
public interface IEventConsumer : IAsyncDisposable
{
    /// <summary>
    /// Unique identifier for this consumer.
    /// </summary>
    string ConsumerId { get; }

    /// <summary>
    /// Register a processor to receive events.
    /// </summary>
    void RegisterProcessor(IEventProcessor processor);

    /// <summary>
    /// Start consuming events and routing them to processors.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop consuming events gracefully.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}
