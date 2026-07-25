using System.Collections.Immutable;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// One shard of a module: an id, and the backend that shard's data lives in.
/// </summary>
/// <param name="ShardId">
/// Identifies the shard in configuration, in the catalog, in metrics and in the CLI. Stable —
/// it is written into the catalog next to every tenant assigned to this shard, so renaming a
/// shard strands those tenants.
/// </param>
/// <param name="Backend">
/// The shard's storage. Built from the module's own backend declaration, so a shard inherits
/// the module's schema and pool settings unless it overrides them.
/// </param>
public sealed record ShardDeclaration(string ShardId, IAlbertoBackendDescriptor Backend);

/// <summary>
/// How a module's tenants are laid out. An unsharded module — the default, and what
/// <c>.WithTenancy()</c> with no argument produces — leaves every property here at its default
/// and keeps all tenants in one database, separated by the <c>tenant_id</c> column.
/// </summary>
/// <remarks>
/// Sharding sits above row-level tenancy rather than replacing it: the shard chooses the
/// database, and the tenant column still filters within it. A shard holding a single tenant
/// therefore uses the same multi-tenant schema as one holding a hundred.
/// </remarks>
public sealed record TenancyDefinition
{
    /// <summary>Every declared shard, in declaration order. Empty for an unsharded module.</summary>
    public ImmutableArray<ShardDeclaration> Shards { get; init; } = [];

    /// <summary>
    /// Where a tenant with no catalog row is placed. Null means an unmapped tenant is an error
    /// rather than an implicit assignment.
    /// </summary>
    public string? DefaultShardId { get; init; }

    /// <summary>
    /// The control database holding the tenant-to-shard catalog. Required once shards are
    /// declared; it is deliberately not one of the shards, so no shard is load-bearing for
    /// routing to the others.
    /// </summary>
    public IAlbertoBackendDescriptor? Catalog { get; init; }

    /// <summary>How often the resolver re-reads the whole catalog. Default 30 seconds.</summary>
    public TimeSpan CatalogRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shard ids that appear under <c>Tenancy:Shards</c> in configuration but were never declared
    /// in code. Reported as <c>ALB0015</c> rather than honoured.
    /// </summary>
    /// <remarks>
    /// Services are registered per shard while the service collection is being built, which is
    /// before configuration is read, so a shard that exists only in configuration has no data
    /// source, no migration and no control loops — it could never serve a request. Configuration
    /// tunes the shards a module declares; adding one is a code change.
    /// </remarks>
    public ImmutableArray<string> UndeclaredConfiguredShards { get; init; } = [];

    /// <summary>Whether this module routes its tenants across several databases.</summary>
    public bool IsSharded => !Shards.IsDefaultOrEmpty && Shards.Length > 0;

    /// <summary>Returns the declared shard ids.</summary>
    public IEnumerable<string> ShardIds => Shards.Select(s => s.ShardId);
}
