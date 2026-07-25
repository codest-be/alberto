namespace Alberto.Dcb.Tenancy;

/// <summary>
/// The backend counterpart of <see cref="ShardRoutingEventStore"/>, registered under the logical
/// module key so code that depends on <see cref="IEventStoreBackend"/> directly keeps working
/// once a module is sharded.
/// </summary>
/// <remarks>
/// Consumers do not come through here. A control loop is registered per shard and resolves that
/// shard's own backend under its shard key, which is what lets each shard keep its own
/// checkpoints against its own <c>position</c> sequence.
/// </remarks>
public sealed class ShardRoutingEventStoreBackend(
    string moduleKey,
    ITenantAccessor tenantAccessor,
    TenantShardResolver resolver,
    Func<string, IEventStoreBackend> shardBackends) : IEventStoreBackend
{
    /// <inheritdoc />
    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await backend.StreamAsync(query, afterPosition, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await backend.StreamAllAsync(afterPosition, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await backend.AppendAsync(events, dcbQuery, expectedPosition, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
    {
        var backend = await ForCurrentTenantAsync(cancellationToken).ConfigureAwait(false);
        return await backend.GetLastPositionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IEventStoreBackend> ForCurrentTenantAsync(CancellationToken cancellationToken)
    {
        var shardId = await resolver.ResolveAsync(tenantAccessor.TenantId, cancellationToken)
            .ConfigureAwait(false);

        return shardBackends(ShardKey.Compose(moduleKey, shardId));
    }
}
