namespace Alberto.Dcb.Tenancy;

/// <summary>
/// The <see cref="IEventStore"/> a sharded module hands to application code: it picks the
/// tenant's database, then delegates the whole call to that shard's own store.
/// </summary>
/// <remarks>
/// Routing sits at the store rather than the backend because a shard's <see cref="IEventStore"/>
/// already has that shard's inline projections and post-append handlers attached, and those must
/// stay shard-local — an inline projection writes its state into the same database it read from.
/// <para>
/// The shard is resolved inside each call, not when this instance is built. Constructor-time
/// resolution would have to be synchronous and would freeze the answer for the lifetime of the
/// scope; resolving per call keeps the catalog authoritative and lets the cache be nothing more
/// than an optimisation.
/// </para>
/// <para>
/// Routing composes with row-level tenancy rather than replacing it. This picks the database;
/// the shard's own tenant decorator still filters on <c>tenant_id</c> inside it, so a shard
/// holding several tenants isolates them exactly as an unsharded module does.
/// </para>
/// </remarks>
public sealed class ShardRoutingEventStore(
    string moduleKey,
    ITenantAccessor tenantAccessor,
    TenantShardResolver resolver,
    Func<string, IEventStore> shardStores) : IEventStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var store = await ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await store.AppendAsync(events, dcbQuery, expectedPosition, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var store = await ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await store.StreamAsync(query, afterPosition, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Still one shard's worth of events: <c>afterPosition</c> is a per-database sequence, so a
    /// union across shards would order by numbers from unrelated sequences and silently skip
    /// events on the next page. Reading every shard is a fan-out the caller has to write, with
    /// its own per-shard cursors.
    /// </remarks>
    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var store = await ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await store.StreamAllAsync(afterPosition, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
    {
        var store = await ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetLastPositionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IEventStore> ForCurrentTenantAsync(CancellationToken cancellationToken)
    {
        // TenantId throws its own "no tenant in context" error, which is the right one to
        // surface: a sharded module cannot answer any question without knowing the tenant.
        var shardId = await resolver.ResolveAsync(tenantAccessor.TenantId, cancellationToken)
            .ConfigureAwait(false);

        return shardStores(ShardKey.Compose(moduleKey, shardId));
    }
}
