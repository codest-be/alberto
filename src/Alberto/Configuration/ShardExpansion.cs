namespace Alberto.Configuration;

/// <summary>
/// Turns one sharded module declaration into the per-shard declarations that actually register
/// services.
/// </summary>
/// <remarks>
/// This is the whole of sharding's effect on registration. Every per-module service — the data
/// source, checkpoints, dead letters, leases, migrations, control loops, projections — is
/// registered by replaying the module's declarations against one
/// <see cref="AlbertoModuleContext"/>, keyed by that context's module key. Replaying the same
/// list once per shard against a context whose key is <c>module#shard</c> therefore produces a
/// complete, independent set per shard, without a single one of those registrations knowing that
/// shards exist.
/// </remarks>
internal static class ShardExpansion
{
    /// <summary>
    /// Returns <paramref name="definition"/> as it applies to one shard: the shard's backend in
    /// place of the module's template, and its own service key.
    /// </summary>
    /// <remarks>
    /// The shard copy carries an empty <see cref="AlbertoModuleDefinition.Tenancy"/>. It keeps
    /// <c>TenancyEnabled</c>, because a shard is still row-level multi-tenant inside itself, but
    /// it is not itself sharded — and dropping the shard list here is also what keeps startup
    /// validation from reporting every sharding problem once per shard.
    /// </remarks>
    internal static AlbertoModuleDefinition ForShard(
        AlbertoModuleDefinition definition,
        ShardDeclaration shard) =>
        definition with
        {
            ShardId = shard.ShardId,
            Backend = shard.Backend,
            Tenancy = new TenancyDefinition(),
        };

    /// <summary>
    /// Returns <paramref name="definition"/> as it applies to the shard with
    /// <paramref name="shardId"/>, or null when that shard is no longer declared.
    /// </summary>
    /// <remarks>
    /// Nullable because a shard's options are bound after configuration has been applied, and
    /// configuration is allowed to change the shard list. A shard that configuration removed
    /// keeps its now-inert options instance rather than throwing during startup binding.
    /// </remarks>
    internal static AlbertoModuleDefinition? ForShard(AlbertoModuleDefinition definition, string shardId)
    {
        foreach (var shard in definition.Tenancy.Shards)
        {
            if (string.Equals(shard.ShardId, shardId, StringComparison.Ordinal))
                return ForShard(definition, shard);
        }

        return null;
    }
}
