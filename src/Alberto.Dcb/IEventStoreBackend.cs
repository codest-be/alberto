namespace Alberto.Dcb;

/// <summary>
/// Core interface for event store backends.
/// Implementations handle persistence and retrieval of events with DCB consistency guarantees.
/// </summary>
public interface IEventStoreBackend
{
    /// <summary>
    /// Reads events for a tenant matching the specified query.
    /// </summary>
    /// <param name="tenantId">The tenant to read events for.</param>
    /// <param name="query">The query criteria (types and/or tags). Use <see cref="DcbQuery.Empty"/> for all events.</param>
    /// <param name="afterPosition">Only return events with position greater than this value (exclusive). Use 0 for all.</param>
    /// <param name="limit">Maximum number of events to return. Null for no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of matching events ordered by global position.</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        string tenantId,
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads all events across all tenants (for system-wide subscriptions/projections).
    /// </summary>
    /// <param name="afterPosition">Only return events with position greater than this value (exclusive). Use 0 for all.</param>
    /// <param name="limit">Maximum number of events to return. Null for no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of all events ordered by global position.</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamGlobal(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends events to the store with an optional DCB consistency check.
    /// </summary>
    /// <param name="tenantId">The tenant to append events for.</param>
    /// <param name="events">The events to append.</param>
    /// <param name="dcbQuery">
    /// Optional DCB query for consistency check. If provided along with <paramref name="expectedPosition"/>,
    /// the append will fail if any event matching this query exists after the expected position.
    /// </param>
    /// <param name="expectedPosition">
    /// The expected last position for the DCB check. Only used when <paramref name="dcbQuery"/> is provided.
    /// Use 0 to ensure no matching events exist.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The appended events with their assigned global positions.</returns>
    /// <exception cref="DcbConflictException">
    /// Thrown when events matching the DCB query exist after the expected position.
    /// </exception>
    Task<IReadOnlyCollection<IEventEnvelope>> Append(
        string tenantId,
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last (highest) global position for a tenant.
    /// Returns 0 if no events exist for the tenant.
    /// </summary>
    /// <param name="tenantId">The tenant to get the last position for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last global position, or 0 if no events exist.</returns>
    Task<long> GetLastPosition(
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last (highest) global position across all tenants.
    /// Returns 0 if no events exist.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last global position, or 0 if no events exist.</returns>
    Task<long> GetLastPositionGlobal(
        CancellationToken cancellationToken = default);
}
