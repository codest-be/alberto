namespace Alberto.Subscriptions;

/// <summary>
/// Durable record of the failure that faulted a processor: what threw, when, and — when the
/// fault happened dispatching a specific event — where in the log it happened.
/// </summary>
/// <remarks>
/// <see cref="Position"/>, <see cref="EventType"/> and <see cref="TenantId"/> are null when the
/// fault did not occur dispatching a single known event (batch dispatch, checkpoint I/O,
/// polling). The stack trace is stored untruncated, matching the dead-letter table.
/// </remarks>
internal sealed record ProcessorFaultRecord(
    DateTimeOffset FaultedAt,
    string Message,
    string? StackTrace,
    long? Position,
    string? EventType,
    string? TenantId);

/// <summary>
/// Optional capability of a checkpoint store: persist the reason a processor faulted next to
/// its checkpoint, so "stopped, restart to retry" survives the process that logged it.
/// </summary>
/// <remarks>
/// <see cref="ControlLoop"/> discovers it with an <c>as</c> cast on its
/// <see cref="ICheckpointStore"/> — a store that does not implement it changes nothing.
/// A fault is cleared by the next run's first successful checkpoint save, and by the
/// operator escape hatches (<see cref="ICheckpointStore.ResetAsync"/> removes the row,
/// <see cref="ICheckpointStore.RewindAsync"/> clears the fault columns: a rewind is a
/// retry intent).
/// </remarks>
internal interface IProcessorFaultStore
{
    Task RecordFaultAsync(string processorId, ProcessorFaultRecord fault, CancellationToken ct = default);

    Task ClearFaultAsync(string processorId, CancellationToken ct = default);

    /// <summary>The recorded fault, or null when the processor has none.</summary>
    Task<ProcessorFaultRecord?> GetFaultAsync(string processorId, CancellationToken ct = default);
}
