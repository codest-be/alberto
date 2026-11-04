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

    /// <summary>How the consume span should relate to the event's append trace.</summary>
    public TraceParenting TraceParenting { get; init; }

    public int Attempt { get; set; }
    public bool DeadLettered { get; set; }

    /// <summary>
    /// Whether the dead-letter row for a <see cref="DeadLettered"/> event was actually written.
    /// False when dead-lettering is disabled or the write failed and was swallowed.
    /// </summary>
    internal bool DeadLetterStored { get; set; }

    /// <summary>
    /// Set by the dead-letter retry loop to the redeliveries the row has had so far. Null on the
    /// first delivery of an event.
    /// </summary>
    internal int? PriorRedeliveries { get; init; }

    /// <summary>Told when this event starts and stops halting its processor. Null outside a control loop.</summary>
    internal IHaltObserver? HaltObserver { get; init; }

    public Exception? LastError { get; set; }
    public CancellationToken CancellationToken { get; init; }
}
