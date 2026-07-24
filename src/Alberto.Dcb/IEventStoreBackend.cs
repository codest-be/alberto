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
    Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads all events (for subscriptions/projections).
    /// In multi-tenant mode, returns events across all tenants.
    /// </summary>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
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
    Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last (highest) global position across all events.
    /// Returns 0 if no events exist.
    /// </summary>
    Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default);
}
