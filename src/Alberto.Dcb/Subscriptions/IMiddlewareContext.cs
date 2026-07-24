namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Shared contract for middleware context types.
/// Implemented by both <see cref="ConsumeEventContext"/> and
/// <see cref="BatchConsumeContext"/> so that cross-cutting middleware
/// (retry, error policy, dead-letter) can operate over a single interface
/// instead of duplicating logic across two parallel paths.
/// </summary>
public interface IMiddlewareContext
{
    /// <summary>The ID of the processor that owns this pipeline.</summary>
    string ProcessorId { get; }

    /// <summary>The module key that scopes events for this processor.</summary>
    string ModuleKey { get; }

    /// <summary>
    /// Current attempt number (1-based). Updated by retry middleware
    /// before each execution attempt.
    /// </summary>
    int Attempt { get; set; }

    /// <summary>
    /// The last exception thrown by the inner pipeline, or
    /// <see langword="null"/> after a successful attempt.
    /// </summary>
    Exception? LastError { get; set; }

    /// <summary>Propagates cancellation from the subscription owner.</summary>
    CancellationToken CancellationToken { get; }
}
