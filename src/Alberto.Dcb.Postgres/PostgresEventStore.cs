using Alberto.Dcb.Subscriptions;
using Npgsql;

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
    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var appended = await _backend.Append(events, dcbQuery, expectedPosition, cancellationToken);

        if (appended.Count > 0 && _inlineProjections.Count > 0)
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
        }

        return appended;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query, long afterPosition = 0,
        int? limit = null, CancellationToken cancellationToken = default)
        => _backend.Stream(query, afterPosition, limit, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0, int? limit = null, CancellationToken cancellationToken = default)
        => _backend.StreamAll(afterPosition, limit, cancellationToken);

    /// <inheritdoc/>
    public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
        => _backend.GetLastPosition(cancellationToken);
}
