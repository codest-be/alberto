using Alberto.Subscriptions;

namespace Alberto;

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
        // A non-null expectedPosition requests a DCB conflict check, so the query has to say what
        // to check. A query naming no types and no tags matches nothing in either index, so no
        // conflict is ever found and the boundary is silently switched off — the exact lost update
        // this library exists to prevent, with no exception and no log line to notice it by.
        //
        // The two ways to arrive here are different mistakes and get different messages. Emptiness
        // that was asked for by name is a coherent request we decline; emptiness that merely
        // happened is a bug at the call site. Only DcbQuery.All can tell us which.
        if (expectedPosition.HasValue)
        {
            if (dcbQuery is null || !dcbQuery.MatchesAll && dcbQuery.IsEmpty)
                throw new ArgumentException(
                    "A consistency check needs a DcbQuery naming at least one event type or tag; " +
                    "this one names neither, so there is nothing it could detect a conflict on. " +
                    "The usual cause is a types or tags collection that came back empty at runtime, " +
                    "or a DcbQuery.Empty builder chain that was never narrowed. " +
                    "For an unconditional append, omit the expectedPosition argument.",
                    nameof(dcbQuery));

            if (dcbQuery.MatchesAll)
                throw new ArgumentException(
                    "DcbQuery.All cannot be used as a consistency check. A boundary spanning the " +
                    "whole log conflicts with every concurrent append, which serialises every " +
                    "writer in the store rather than only the ones that actually interact — the " +
                    "opposite of what dynamic consistency boundaries are for. Name the event types " +
                    "or tags this decision genuinely depends on. DcbQuery.All remains valid for " +
                    "reads; for an unconditional append, omit the expectedPosition argument.",
                    nameof(dcbQuery));
        }

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
