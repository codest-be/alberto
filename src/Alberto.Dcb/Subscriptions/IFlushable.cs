namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Interface for processors that batch changes and need explicit flushing.
/// Implements interface segregation - not all processors need this capability.
/// </summary>
public interface IFlushable
{
    /// <summary>
    /// Flushes any pending changes to the underlying store.
    /// Called by the consumer after processing a batch of events.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task FlushAsync(CancellationToken ct = default);
}
