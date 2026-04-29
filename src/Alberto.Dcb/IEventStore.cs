using Alberto.Dcb.Subscriptions;

#pragma warning disable CS0618 // Obsolete projection types used intentionally for backward-compatibility
namespace Alberto.Dcb;

/// <summary>
/// High-level event store with support for inline projections and post-append handlers.
/// Wraps <see cref="IEventStoreBackend"/> and runs registered projections and handlers immediately after append.
/// In multi-tenant mode, tenant scoping is handled transparently by the backend decorator.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Registers an inline projection that runs immediately after events are appended.
    /// Uses the same <see cref="Projection{TState}"/> definition as async projections,
    /// but updates state synchronously for lower latency.
    /// </summary>
    /// <typeparam name="TState">The projection state type.</typeparam>
    /// <typeparam name="TProjection">The projection implementation type.</typeparam>
    /// <param name="stateStore">The state store for persisting projection state.</param>
    void RegisterInlineProjection<TState, TProjection>(IStateStore<TState> stateStore)
        where TProjection : Projection<TState>, new()
        where TState : new();

    /// <summary>
    /// Registers an inline projection that runs immediately after events are appended.
    /// Lower-level overload that accepts any <see cref="IInlineProjection"/> implementation —
    /// used by declaration-based projection wiring.
    /// </summary>
    /// <param name="projection">The inline projection to register.</param>
    void RegisterInlineProjection(IInlineProjection projection);

    /// <summary>
    /// Registers a post-append handler that runs immediately after events are appended
    /// and inline projections have completed. Used by <see cref="ReactorMode.Sync"/> reactors.
    /// </summary>
    /// <param name="handler">The handler to register.</param>
    void RegisterPostAppendHandler(IPostAppendHandler handler);

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
