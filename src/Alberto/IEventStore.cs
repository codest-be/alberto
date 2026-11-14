namespace Alberto;

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
    /// Reads one materialised batch of events matching the query, in position order.
    /// In multi-tenant mode, automatically scoped to the current tenant.
    /// </summary>
    /// <remarks>
    /// Fewer than <paramref name="limit"/> events means nothing further matched after
    /// <paramref name="afterPosition"/>. To read everything without holding it all in memory, use
    /// <see cref="StreamAsync"/>.
    /// </remarks>
    /// <param name="query">The query criteria.</param>
    /// <param name="afterPosition">Only return events with position greater than this value.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyCollection<IEventEnvelope>> ReadAsync(
        DcbQuery query,
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one materialised batch of all events (for system-wide queries), in position order.
    /// In multi-tenant mode, returns events across all tenants.
    /// </summary>
    /// <param name="afterPosition">Only return events with position greater than this value.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyCollection<IEventEnvelope>> ReadAllAsync(
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every event matching the query, in position order, without materialising the
    /// result. In multi-tenant mode, automatically scoped to the current tenant.
    /// </summary>
    /// <remarks>
    /// The backend keeps its read open until enumeration ends, so enumerate promptly and dispose
    /// the enumerator (<c>await foreach</c> does).
    /// </remarks>
    /// <param name="query">The query criteria.</param>
    /// <param name="afterPosition">Only return events with position greater than this value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<IEventEnvelope> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every event, in position order, without materialising the result.
    /// In multi-tenant mode, returns events across all tenants.
    /// </summary>
    /// <param name="afterPosition">Only return events with position greater than this value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<IEventEnvelope> StreamAllAsync(
        long afterPosition = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last global position across all events.
    /// </summary>
    Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default);
}
