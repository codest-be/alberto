namespace Alberto.Dcb;

/// <summary>
/// Runtime surface of the event store: append events and read them back.
/// In multi-tenant mode, tenant scoping is handled transparently by the backend decorator.
/// </summary>
/// <remarks>
/// Setup-time operations (registering inline projections and post-append handlers) are on
/// <see cref="IEventStoreConfigurator"/>, which <see cref="EventStore"/> also implements.
/// Builder and registration code should resolve <see cref="IEventStoreConfigurator"/> at
/// startup; runtime consumers should depend only on <see cref="IEventStore"/>.
/// </remarks>
public interface IEventStore
{
    /// <summary>
    /// Appends events to the store and runs inline projections immediately after.
    /// In multi-tenant mode, the tenant is resolved automatically from the current request context.
    /// </summary>
    /// <param name="events">The events to append.</param>
    /// <param name="dcbQuery">Optional DCB query for consistency check.</param>
    /// <param name="expectedPosition">The expected last position for the DCB check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The appended events with their assigned global positions.</returns>
    /// <exception cref="DcbConflictException">
    /// Thrown when events matching the DCB query exist after the expected position.
    /// </exception>
    Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads events matching the specified query.
    /// In multi-tenant mode, automatically scoped to the current tenant.
    /// </summary>
    /// <param name="query">The query criteria.</param>
    /// <param name="afterPosition">Only return events with position greater than this value.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of matching events ordered by global position.</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads all events (for system-wide queries).
    /// In multi-tenant mode, returns events across all tenants.
    /// </summary>
    /// <param name="afterPosition">Only return events with position greater than this value.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of all events ordered by global position.</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last global position across all events.
    /// </summary>
    Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default);
}
