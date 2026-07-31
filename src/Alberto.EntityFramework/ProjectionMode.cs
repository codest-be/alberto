namespace Alberto.EntityFramework;

/// <summary>
/// Controls how an EF projection processes events.
/// </summary>
public enum ProjectionMode
{
    /// <summary>
    /// Projection runs via the background polling consumer with its own checkpoint.
    /// Default. Processes events with a small lag (~polling interval), can be rebuilt independently,
    /// and a projection failure does not affect the originating mutation.
    /// </summary>
    Async,

    /// <summary>
    /// Projection runs synchronously immediately after <see cref="Alberto.IEventStore.AppendAsync"/>
    /// commits, before returning to the caller. Provides read-your-writes consistency at the cost of
    /// adding the projection's write latency to the mutation and coupling its failures to the response.
    /// Best for narrow per-user projections that the caller will immediately re-query.
    /// Inline projections do not register an <c>IProjectionStateClearer</c> and cannot be rebuilt
    /// via the standard rebuild pipeline.
    /// </summary>
    Inline,
}
