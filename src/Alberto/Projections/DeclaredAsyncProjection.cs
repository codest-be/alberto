using System.Collections.Concurrent;
using Alberto.Subscriptions;

namespace Alberto.Projections;

/// <summary>
/// Internal processor that executes a <see cref="ProjectionDeclaration{TState}"/> asynchronously.
/// No reflection — all dispatch is delegate-based.
/// </summary>
/// <remarks>
/// <para>
/// One control loop consumes every tenant's events, but a state store's tenancy is fixed when it
/// is built — it decides the SQL the store emits and the key it writes under. So this processor
/// holds a store <em>per tenant</em>, built on demand from the tenant-keyed factory and kept for
/// the process lifetime. A projection that keeps one document set per tenant gets isolation for
/// free; one that blends every tenant into a single document builds its stores over a shared
/// backing store and passes
/// <see cref="Alberto.Tenancy.TenantScope.CrossTenantFor"/> as their tenant.
/// </para>
/// <para>
/// A batch spanning several tenants is therefore split — but into <em>contiguous runs</em>, not
/// grouped by tenant. Runs are applied in order, so events reach the projection in global
/// position order exactly as they would have unsplit. Grouping would have been cheaper and is
/// what a per-tenant projection could tolerate, but it reorders events across tenants, which a
/// cross-tenant projection tracking anything order-sensitive cannot. The cost is one transaction
/// per run instead of one per batch; batches that carry a single tenant, which is the common
/// case, are unaffected.
/// </para>
/// <para>
/// The factory is the only way in: there is deliberately no overload taking a
/// <c>Func&lt;IStateStore&lt;TState&gt;&gt;</c>. Handing every tenant one store is legitimate — a
/// module without tenancy has nothing to route on, and an EF projection carries its tenant as a
/// column the projection body writes — but it is also exactly the shape that silently fails every
/// write against a tenant-keyed table (<c>42P10</c>). Writing <c>_ =&gt; store</c> costs three
/// characters and puts the discarded tenant at the call site, where a reader can judge it.
/// </para>
/// </remarks>
internal sealed class DeclaredAsyncProjection<TState> : IBatchableProcessor, IProcessorLifecycle, IAsyncDisposable
    where TState : new()
{
    /// <summary>
    /// Stand-in dictionary key for the null tenant of a module without tenancy. Leads with NUL so
    /// it cannot collide with a real tenant id — written as the escape rather than a literal NUL
    /// byte, which would make this file binary to git and invisible to grep.
    /// </summary>
    private const string NoTenant = "\0none";

    private readonly ProjectionDeclaration<TState> _declaration;
    private readonly Func<string?, IStateStore<TState>> _stateStoreFactory;
    private readonly Func<IReadOnlyList<IEventEnvelope>, CancellationToken, Task>? _afterCommit;
    private readonly EventSerializer? _serializer;
    private readonly ConcurrentDictionary<string, IStateStore<TState>> _stateStores = new();
    private volatile bool _isActive = true;
    private volatile bool _isRebuilding;
    private bool _disposed;

    public DeclaredAsyncProjection(
        ProjectionDeclaration<TState> declaration,
        Func<string?, IStateStore<TState>> stateStoreFactory,
        string? processorIdOverride = null,
        Func<IReadOnlyList<IEventEnvelope>, CancellationToken, Task>? afterCommit = null,
        EventSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(stateStoreFactory);
        _declaration = declaration;
        _stateStoreFactory = stateStoreFactory;
        _afterCommit = afterCommit;
        _serializer = serializer;
        ProcessorId = processorIdOverride ?? declaration.ProcessorId;
    }

    /// <inheritdoc/>
    public string ProcessorId { get; }

    /// <inheritdoc/>
    public bool IsActive
    {
        get => _isActive;
        set => _isActive = value;
    }

    /// <inheritdoc/>
    public bool IsRebuilding
    {
        get => _isRebuilding;
        set => _isRebuilding = value;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => _declaration.HandledEventTypes;

    /// <summary>
    /// The store for one tenant, built once and reused. Built lazily rather than up front
    /// because the set of tenants is not known until their events arrive.
    /// </summary>
    private IStateStore<TState> GetStore(string? tenantId) =>
        _stateStores.GetOrAdd(tenantId ?? NoTenant, _ => _stateStoreFactory(tenantId));

    /// <inheritdoc/>
    public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default)
        => ProcessTenantRunAsync(@event.TenantId, [@event], cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Splits the batch into contiguous runs of the same tenant and applies each run against
    /// that tenant's store. A batch that carries one tenant — the common case — is one run and
    /// behaves exactly as it did before stores became per-tenant.
    /// </remarks>
    public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default)
    {
        var start = 0;
        while (start < events.Count)
        {
            var tenantId = events[start].TenantId;

            var end = start + 1;
            while (end < events.Count &&
                   string.Equals(events[end].TenantId, tenantId, StringComparison.Ordinal))
            {
                end++;
            }

            // Slicing an IReadOnlyList needs a copy; skip it for the single-run case, which is
            // every batch in a module without tenancy and most batches in one with it.
            var run = start == 0 && end == events.Count
                ? events
                : events.Skip(start).Take(end - start).ToList();

            await ProcessTenantRunAsync(tenantId, run, cancellationToken);
            start = end;
        }
    }

    private async Task ProcessTenantRunAsync(
        string? tenantId,
        IReadOnlyList<IEventEnvelope> events,
        CancellationToken cancellationToken)
    {
        var batch = new ProjectionBatch<TState>(_declaration, events, _serializer);
        var documentIds = batch.DocumentIds;
        if (documentIds.Count == 0) return;

        var stateStore = GetStore(tenantId);
        var states = await stateStore.LoadManyAsync(documentIds, cancellationToken);

        var changes = batch.Fold(states);

        if (changes.Upserts.Count > 0 || changes.Deletes.Count > 0)
        {
            await stateStore.ApplyChangesAsync(changes.Upserts, changes.Deletes, cancellationToken);
            if (_afterCommit is not null)
                await _afterCommit(batch.Handled, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _isActive = false;

        foreach (var store in _stateStores.Values)
        {
            if (store is not IAsyncDisposable disposable) continue;

            try { await disposable.DisposeAsync(); }
            catch { /* Best effort */ }
        }
    }
}
