using System.Diagnostics.CodeAnalysis;

namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Resolves the current tenant's shard and returns the keyed service for it.
/// </summary>
/// <remarks>
/// Shared by <see cref="ShardRoutingEventStore"/> and <see cref="ShardRoutingEventStoreBackend"/>
/// so the tenant→shard lookup lives in exactly one place.
/// </remarks>
[Experimental("ALB9001")]
internal sealed class ShardRouter<T>(
    string moduleKey,
    ITenantAccessor tenantAccessor,
    TenantShardResolver resolver,
    Func<string, T> shardLookup)
{
    internal async ValueTask<T> ForCurrentTenantAsync(CancellationToken cancellationToken)
    {
        // TenantId throws its own "no tenant in context" error, which is the right one to
        // surface: a sharded module cannot answer any question without knowing the tenant.
        var shardId = await resolver.ResolveAsync(tenantAccessor.TenantId, cancellationToken)
            .ConfigureAwait(false);

        return shardLookup(ShardKey.Compose(moduleKey, shardId));
    }
}
