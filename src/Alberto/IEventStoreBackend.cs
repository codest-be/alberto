namespace Alberto;

/// <summary>
/// What a backend author implements: the whole of <see cref="IEventStore"/> (append, batch reads,
/// streaming reads, last position) plus the type-filtered read the control loop needs.
/// Application code depends on <see cref="IEventStore"/>; <see cref="EventStore"/> wraps a backend
/// and adds argument validation, inline projections and post-append handlers on top.
/// In single-tenant mode (default), no tenant scoping is applied.
/// In multi-tenant mode (opt-in via .WithTenancy()), tenant scoping is handled by the decorator.
/// </summary>
public interface IEventStoreBackend : IEventStore
{
    /// <summary>
    /// As <see cref="IEventStore.ReadAllAsync"/>, for a reader that only wants <paramref name="eventTypes"/>,
    /// so a backend can leave the other events on the server.
    /// </summary>
    /// <remarks>
    /// Implementations must filter: only events of <paramref name="eventTypes"/> come back, in
    /// position order, and fewer than <paramref name="limit"/> means no further event of these
    /// types was visible after <paramref name="afterPosition"/>.
    /// </remarks>
    Task<IReadOnlyCollection<IEventEnvelope>> ReadAllOfTypesAsync(
        IReadOnlySet<string> eventTypes,
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default);
}
