namespace Alberto.Dcb;

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
    /// Returns global_position values of committed events in (afterPosition, afterPosition + windowSize].
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
}
