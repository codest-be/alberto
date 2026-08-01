using System.Diagnostics.CodeAnalysis;

namespace Alberto.Tenancy;

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
[Experimental("ALB9001")]
public sealed class ShardRoutingEventStore(
    string moduleKey,
    ITenantAccessor tenantAccessor,
    TenantShardResolver resolver,
    Func<string, IEventStore> shardStores) : IEventStore
{
    private readonly ShardRouter<IEventStore> _router = new(moduleKey, tenantAccessor, resolver, shardStores);

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var store = await _router.ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await store.AppendAsync(events, dcbQuery, expectedPosition, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="afterPosition"/> must be a position issued by this tenant's shard.
    /// Positions are per-database sequences; passing a value obtained from a different shard
    /// produces silently wrong results — events may be skipped or repeated.
    /// </remarks>
    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var store = await _router.ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
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
        var store = await _router.ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await store.StreamAllAsync(afterPosition, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The returned position belongs to the current tenant's shard. Each shard maintains its own
    /// independent <c>position</c> sequence starting at 1, so a value from shard A is meaningless
    /// in shard B. Never compare, order, or use a position from one shard as a cursor into
    /// another — the result would silently be nonsense with no runtime error to catch it.
    /// </remarks>
    public async Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
    {
        var store = await _router.ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetLastPositionAsync(cancellationToken).ConfigureAwait(false);
    }
}
