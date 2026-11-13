namespace Alberto;

/// <summary>
/// Backend contract used exclusively by <see cref="Subscriptions.EventStoreHead"/> to track
/// the safe subscription head.
///
/// <para>
/// Separating these two methods from <see cref="IEventStoreBackend"/> lets implementations that
/// serve only head-tracking (e.g. test fakes) avoid implementing the full append/read surface.
/// All concrete backends implement both interfaces.
/// </para>
/// </summary>
public interface IEventStoreHeadBackend
{
    /// <summary>
    /// Returns the global_position values of the next <paramref name="windowSize"/> committed events
    /// after <paramref name="afterPosition"/>, in ascending order. The window is bounded by row count,
    /// not by position range, so a gap in the sequence wider than the window does not yield an empty one.
    /// Lightweight — no event data. Used by <see cref="Subscriptions.EventStoreHead"/> for gap detection.
    /// </summary>
    Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition,
        int windowSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the highest global position that is safe to expose to subscribers:
    /// the newest event whose inserting transaction has committed and is older than
    /// every currently in-flight transaction. This prevents advancing the
    /// subscription head past an append that drew a lower position but has not
    /// committed yet — which a naive contiguous-gap scan could otherwise skip once
    /// later positions commit ahead of it.
    ///
    /// The default returns <see cref="long.MaxValue"/> ("no barrier"), so backends
    /// that assign positions synchronously (e.g. in-memory) impose no clamp and the
    /// caller relies solely on contiguous-gap detection. Decorators must forward to
    /// their inner backend.
    /// </summary>
    Task<long> GetStableHeadAsync(
        long afterPosition,
        CancellationToken cancellationToken = default)
        => Task.FromResult(long.MaxValue);

    /// <summary>
    /// <see cref="GetStableHeadAsync(long, CancellationToken)"/> restricted to positions in
    /// (<paramref name="afterPosition"/>, <paramref name="upToPosition"/>]. The head only ever
    /// asks about the window it has just read, and a backend that knows the upper bound can stop
    /// there rather than search the rest of the log for a barrier that, in a healthy store, does
    /// not exist. A barrier above <paramref name="upToPosition"/> may be reported or not: the
    /// caller treats any result at or above it as "no barrier".
    ///
    /// The default ignores the bound, which is correct for every backend and merely slower.
    /// Decorators must forward to their inner backend.
    /// </summary>
    Task<long> GetStableHeadUpToAsync(
        long afterPosition,
        long upToPosition,
        CancellationToken cancellationToken = default)
        => GetStableHeadAsync(afterPosition, cancellationToken);
}
