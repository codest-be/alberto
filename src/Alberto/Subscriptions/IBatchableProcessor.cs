namespace Alberto.Subscriptions;

/// <summary>
/// Implemented by processors that support batch event processing.
/// When available, the polling consumer will prefer batch processing
/// over the per-event path for better performance.
/// </summary>
public interface IBatchableProcessor : IEventProcessor
{
    /// <summary>
    /// Processes a batch of events in one operation:
    /// 1. LoadMany all affected documents
    /// 2. Apply events in memory
    /// 3. ApplyChanges once for all documents
    /// </summary>
    Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default);
}
