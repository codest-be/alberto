namespace Alberto.Dcb;

/// <summary>
/// Core interface for event store backends.
/// Implementations handle persistence and retrieval of events with DCB consistency guarantees.
/// In single-tenant mode (default), no tenant scoping is applied.
/// In multi-tenant mode (opt-in via .WithTenancy()), tenant scoping is handled by the decorator.
/// </summary>
public interface IEventStoreBackend
{
    /// <summary>
    /// Reads events matching the specified query.
    /// In multi-tenant mode, automatically scoped to the current tenant via TenantAccessor.
    /// </summary>
    Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads all events (for subscriptions/projections).
    /// In multi-tenant mode, returns events across all tenants.
    /// </summary>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamAll(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends events with optional DCB consistency check.
    /// In multi-tenant mode, automatically tagged with current tenant via TenantAccessor.
    /// </summary>
    /// <exception cref="DcbConflictException">
    /// Thrown when events matching the DCB query exist after the expected position.
    /// </exception>
    Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last (highest) global position across all events.
    /// Returns 0 if no events exist.
    /// </summary>
    Task<long> GetLastPosition(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns global_position values of committed events in (afterPosition, afterPosition + windowSize].
    /// Lightweight — no event data. Used by EventStoreHead for gap detection.
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
