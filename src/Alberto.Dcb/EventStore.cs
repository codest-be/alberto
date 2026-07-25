#pragma warning disable CS0618 // Legacy registration overload retained until the obsolete projection module is removed
using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb;

/// <summary>
/// Coordinates event persistence with synchronous projections and post-append handlers.
/// Storage-specific behavior lives behind <see cref="IEventStoreBackend"/>.
/// </summary>
public sealed class EventStore : IEventStore, IEventStoreConfigurator
{
    private readonly IEventStoreBackend _backend;
    private readonly List<IInlineProjection> _inlineProjections;
    private readonly List<IPostAppendHandler> _postAppendHandlers;

    /// <summary>
    /// Creates an event store over the supplied storage adapter.
    /// </summary>
    public EventStore(
        IEventStoreBackend backend,
        IEnumerable<IInlineProjection>? inlineProjections = null,
        IEnumerable<IPostAppendHandler>? postAppendHandlers = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _inlineProjections = inlineProjections?.ToList() ?? [];
        _postAppendHandlers = postAppendHandlers?.ToList() ?? [];
    }

    /// <inheritdoc/>
    public void RegisterInlineProjection<TState, TProjection>(IStateStore<TState> stateStore)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
        _inlineProjections.Add(new InlineProjection<TState, TProjection>(stateStore));
    }

    /// <inheritdoc/>
    public void RegisterInlineProjection(IInlineProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        _inlineProjections.Add(projection);
    }

    /// <inheritdoc/>
    public void RegisterPostAppendHandler(IPostAppendHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _postAppendHandlers.Add(handler);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var appended = await _backend.AppendAsync(
            events,
            dcbQuery,
            expectedPosition,
            cancellationToken);

        if (appended.Count == 0)
            return appended;

        var appendedList = appended.ToList();

        foreach (var projection in _inlineProjections)
        {
            var relevant = appendedList
                .Where(e => projection.HandledEventTypes.Contains(e.EventType.Id))
                .ToList();

            if (relevant.Count > 0)
                await projection.ProcessAsync(relevant, cancellationToken);
        }

        foreach (var handler in _postAppendHandlers)
        {
            var relevant = appendedList
                .Where(e => handler.HandledEventTypes.Contains(e.EventType.Id))
                .ToList();

            if (relevant.Count > 0)
                await handler.ProcessAsync(relevant, cancellationToken);
        }

        return appended;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        _backend.StreamAsync(query, afterPosition, limit, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        _backend.StreamAllAsync(afterPosition, limit, cancellationToken);

    /// <inheritdoc/>
    public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default) =>
        _backend.GetLastPositionAsync(cancellationToken);
}
