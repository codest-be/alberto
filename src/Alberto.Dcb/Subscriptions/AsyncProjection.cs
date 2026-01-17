using System.Collections.Concurrent;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal processor that applies a projection asynchronously via a consumer.
/// Uses IStateStore for persistence - no inheritance required.
/// </summary>
internal sealed class AsyncProjection<TState, TProjection> : IEventProcessor
    where TProjection : Projection<TState>, new()
    where TState : new()
{
    private readonly Func<string, IStateStore<TState>> _stateStoreFactory;
    private readonly ConcurrentDictionary<string, IStateStore<TState>> _stateStoreCache = new();
    private readonly TProjection _projection = new();

    public AsyncProjection(Func<string, IStateStore<TState>> stateStoreFactory, string processorId)
    {
        _stateStoreFactory = stateStoreFactory ?? throw new ArgumentNullException(nameof(stateStoreFactory));
        ProcessorId = processorId ?? throw new ArgumentNullException(nameof(processorId));
    }

    /// <inheritdoc/>
    public string ProcessorId { get; }

    /// <inheritdoc/>
    public bool IsActive { get; set; } = true;

    /// <inheritdoc/>
    public bool IsRebuilding { get; set; }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _projection.HandledEventTypes;

    /// <inheritdoc/>
    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        var docId = _projection.GetDocumentId(@event);
        var tenantId = @event.TenantId;

        // Get or create state store for this tenant
        var stateStore = _stateStoreCache.GetOrAdd(tenantId, _stateStoreFactory);

        // Load current state
        var states = await stateStore.LoadManyAsync([docId], transaction: null, ct);
        var state = states.GetValueOrDefault(docId) ?? new TState();

        // Apply event
        var result = _projection.Apply(state, @event);

        // Persist change
        switch (result)
        {
            case ProjectionResult<TState>.Set s:
                await stateStore.ApplyChangesAsync(
                    new Dictionary<string, TState> { [docId] = s.State },
                    [],
                    transaction: null,
                    ct);
                break;
            case ProjectionResult<TState>.Delete:
                await stateStore.ApplyChangesAsync(
                    new Dictionary<string, TState>(),
                    [docId],
                    transaction: null,
                    ct);
                break;
            // Unchanged: no database operation
        }
    }
}
