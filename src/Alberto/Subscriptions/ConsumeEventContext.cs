namespace Alberto.Subscriptions;

/// <summary>
/// Context passed through the middleware chain for a single event.
/// </summary>
public sealed class ConsumeEventContext : IMiddlewareContext
{
    public required string ProcessorId { get; init; }
    public required string ModuleKey { get; init; }
    public required IEventEnvelope Envelope { get; init; }
    public required bool IsRebuild { get; init; }
    public int Attempt { get; set; }
    public bool DeadLettered { get; set; }

    /// <summary>
    /// Whether the dead-letter row for a <see cref="DeadLettered"/> event was actually written.
    /// False when dead-lettering is disabled or the write failed and was swallowed.
    /// </summary>
    internal bool DeadLetterStored { get; set; }

    public Exception? LastError { get; set; }
    public CancellationToken CancellationToken { get; init; }
}
