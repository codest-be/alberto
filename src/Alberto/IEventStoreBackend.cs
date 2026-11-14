namespace Alberto;

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
    /// As <see cref="StreamAllAsync"/>, for a reader that only wants <paramref name="eventTypes"/>,
    /// so a backend can leave the other events on the server.
    /// </summary>
    /// <remarks>
    /// The filter is a hint, not a guarantee: the default returns every event, so a caller must
    /// still ignore types it did not ask for. What a backend must guarantee is the order and the
    /// limit: events come back in position order, and fewer than <paramref name="limit"/> means
    /// no further event of these types was visible after <paramref name="afterPosition"/>.
    /// </remarks>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamAllOfTypesAsync(
        IReadOnlySet<string> eventTypes,
        long afterPosition,
        int? limit,
        CancellationToken cancellationToken = default)
        => StreamAllAsync(afterPosition, limit, cancellationToken);

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
