namespace Alberto.Subscriptions;

/// <summary>
/// Context passed through the batch middleware chain for a batch of events.
/// </summary>
public sealed class BatchConsumeContext : IMiddlewareContext
{
    public required string ProcessorId { get; init; }
    public required string ModuleKey { get; init; }
    public required IReadOnlyList<IEventEnvelope> Envelopes { get; init; }
    public required bool IsRebuild { get; init; }
    public int Attempt { get; internal set; }
    public int DeadLetteredCount { get; internal set; }

    /// <summary>Told when a single-event batch starts and stops halting its processor.</summary>
    internal IHaltObserver? HaltObserver { get; init; }
    public Exception? LastError { get; internal set; }
    int IMiddlewareContext.Attempt { get => Attempt; set => Attempt = value; }
    Exception? IMiddlewareContext.LastError { get => LastError; set => LastError = value; }
    public CancellationToken CancellationToken { get; init; }
}
