using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IEventStore"/> with inline projection support.
/// Wraps <see cref="IEventStoreBackend"/> and coordinates inline projections during append.
/// Useful for testing and development scenarios.
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly IEventStoreBackend _backend;
    private readonly List<IInlineProjection> _inlineProjections = [];

    public InMemoryEventStore(IEventStoreBackend backend)
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
    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        string tenantId,
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        // Append events to the backend
        var appended = await _backend.Append(
            tenantId,
            events,
            dcbQuery,
            expectedPosition,
            cancellationToken);

        // Run inline projections (no real transaction for in-memory)
        if (appended.Count > 0 && _inlineProjections.Count > 0)
        {
            var appendedList = appended.ToList();
            foreach (var projection in _inlineProjections)
            {
                var relevant = appendedList
                    .Where(e => projection.HandledEventTypes.Contains(e.EventType.Id))
                    .ToList();

                if (relevant.Count > 0)
                {
                    // Pass null for transaction - in-memory doesn't need it
                    await projection.ProcessAsync(relevant, null!, cancellationToken);
                }
            }
        }

        return appended;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        string tenantId,
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return _backend.Stream(tenantId, query, afterPosition, limit, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamGlobalAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return _backend.StreamGlobal(afterPosition, limit, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<long> GetLastPositionAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        return _backend.GetLastPosition(tenantId, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<long> GetLastPositionGlobalAsync(
        CancellationToken cancellationToken = default)
    {
        return _backend.GetLastPositionGlobal(cancellationToken);
    }
}
