using Alberto.Dcb.Subscriptions;

#pragma warning disable CS0618 // Obsolete projection types used intentionally for backward-compatibility
namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IEventStore"/> with inline projection support.
/// Inline projections run immediately after events are appended.
/// </summary>
public sealed class PostgresEventStore : IEventStore
{
    private readonly IEventStoreBackend _backend;
    private readonly List<IInlineProjection> _inlineProjections = [];
    private readonly List<IPostAppendHandler> _postAppendHandlers = [];

    public PostgresEventStore(IEventStoreBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
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
        var appended = await _backend.AppendAsync(events, dcbQuery, expectedPosition, cancellationToken);

        if (appended.Count > 0)
        {
            var appendedList = appended.ToList();

            foreach (var projection in _inlineProjections)
            {
                var relevant = appendedList
                    .Where(e => projection.HandledEventTypes.Contains(e.EventType.Id))
                    .ToList();

                if (relevant.Count > 0)
                    await projection.ProcessAsync(relevant, null, cancellationToken);
            }

            foreach (var handler in _postAppendHandlers)
            {
                var relevant = appendedList
                    .Where(e => handler.HandledEventTypes.Contains(e.EventType.Id))
                    .ToList();

                if (relevant.Count > 0)
                    await handler.ProcessAsync(relevant, cancellationToken);
            }
        }

        return appended;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query, long afterPosition = 0,
        int? limit = null, CancellationToken cancellationToken = default)
        => _backend.StreamAsync(query, afterPosition, limit, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
        => _backend.StreamAllAsync(afterPosition, limit, cancellationToken);

    /// <inheritdoc/>
    public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
        => _backend.GetLastPositionAsync(cancellationToken);
}
