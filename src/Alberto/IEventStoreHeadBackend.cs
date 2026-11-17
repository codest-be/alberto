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
    /// <see cref="GetPositionsAsync"/> plus the <see cref="TransactionHorizon"/> of the snapshot the
    /// positions were read in. A backend whose appends draw positions concurrently (Postgres: tenants
    /// lock separately but share one sequence) reports the horizon, and the head then skips a gap only
    /// once every transaction that could still fill it has ended. The default reports none: positions
    /// are assigned synchronously, so a gap with positions after it is permanent.
    /// Decorators must forward to their inner backend.
    /// </summary>
    async Task<HeadWindow> GetPositionWindowAsync(
        long afterPosition,
        int windowSize,
        CancellationToken cancellationToken = default)
        => new(await GetPositionsAsync(afterPosition, windowSize, cancellationToken), null);

    /// <summary>
    /// Returns the highest global position that is safe to expose to subscribers:
    /// the newest event whose inserting transaction has committed and is older than
    /// every currently in-flight transaction. It holds the head below committed events an
    /// open transaction could still precede, but it does not prove a gap permanent: in a
    /// multi-tenant store it can stop exactly on the position an open append drew. Gaps are
    /// proven with <see cref="GetPositionWindowAsync"/>'s horizon.
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

/// <summary>The next committed positions after a point, and the horizon of the snapshot they were read in (null when the backend has none).</summary>
public sealed record HeadWindow(IReadOnlyList<long> Positions, TransactionHorizon? Horizon);

/// <summary>
/// A snapshot's transaction horizon: every transaction id below <see cref="OldestRunning"/> had ended
/// when it was taken, and none at or above <see cref="NextToStart"/> had started.
/// <see cref="LastPosition"/> is the highest position that snapshot saw (0 when the log is empty).
/// </summary>
public sealed record TransactionHorizon(long OldestRunning, long NextToStart, long LastPosition);
