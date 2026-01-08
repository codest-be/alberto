using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb;

/// <summary>
/// High-level event store with support for inline projections.
/// Wraps <see cref="IEventStoreBackend"/> and coordinates transactional projections.
/// Inline projections run in the same database transaction as event append.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Registers an inline projection that runs in the same transaction as Append.
    /// Inline projections provide strong consistency: events and projections commit together.
    /// Uses the same <see cref="Projection{TState}"/> definition as async projections.
    /// </summary>
    /// <typeparam name="TState">The projection state type.</typeparam>
    /// <typeparam name="TProjection">The projection implementation type.</typeparam>
    /// <param name="stateStore">The state store for persisting projection state.</param>
    void RegisterInlineProjection<TState, TProjection>(IStateStore<TState> stateStore)
        where TProjection : Projection<TState>, new()
        where TState : new();

    /// <summary>
    /// Appends events to the store with inline projections executed in the same transaction.
    /// </summary>
    /// <param name="tenantId">The tenant to append events for.</param>
    /// <param name="events">The events to append.</param>
    /// <param name="dcbQuery">Optional DCB query for consistency check.</param>
    /// <param name="expectedPosition">The expected last position for the DCB check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The appended events with their assigned global positions.</returns>
    /// <exception cref="DcbConflictException">
    /// Thrown when events matching the DCB query exist after the expected position.
    /// </exception>
    Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        string tenantId,
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads events for a tenant matching the specified query.
    /// </summary>
    /// <param name="tenantId">The tenant to read events for.</param>
    /// <param name="query">The query criteria.</param>
    /// <param name="afterPosition">Only return events with position greater than this value.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of matching events ordered by global position.</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        string tenantId,
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads all events across all tenants.
    /// </summary>
    /// <param name="afterPosition">Only return events with position greater than this value.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of all events ordered by global position.</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamGlobalAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last global position for a tenant.
    /// </summary>
    Task<long> GetLastPositionAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last global position across all tenants.
    /// </summary>
    Task<long> GetLastPositionGlobalAsync(
        CancellationToken cancellationToken = default);
}
