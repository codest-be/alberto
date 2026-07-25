using System.Collections.Immutable;
using Alberto.Dcb.Configuration;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Declares which PostgreSQL databases a module's tenants are spread across.
/// Reached through <c>.WithTenancy(t =&gt; t.AcrossPostgresDatabases(s =&gt; ...))</c>.
/// </summary>
/// <remarks>
/// Nothing here opens a connection or reads configuration. Like every other Alberto builder it
/// accumulates a declaration that <c>AddAlberto</c> expands afterwards — once per shard.
/// </remarks>
public sealed class PostgresShardBuilder
{
    private readonly PostgresOptions _template;
    private readonly ImmutableArray<ShardDeclaration>.Builder _shards =
        ImmutableArray.CreateBuilder<ShardDeclaration>();

    private IAlbertoBackendDescriptor? _catalog;
    private string? _defaultShardId;
    private TimeSpan? _refreshInterval;

    internal PostgresShardBuilder(PostgresOptions template) => _template = template;

    /// <summary>
    /// Adds a database that some of this module's tenants live in.
    /// </summary>
    /// <param name="shardId">
    /// Stable identifier for the database, used as the DI service key suffix, in telemetry, and as
    /// the value stored in the catalog. Lowercase letters, digits and underscores; it names a
    /// deployment slot, so keep it out of the connection string's business — <c>db1</c>, <c>eu</c>
    /// and <c>legacy</c> all read better than a hostname you may later change.
    /// </param>
    /// <param name="configure">
    /// Transforms the module's own <c>.WithPostgres(...)</c> options into this shard's. Only what
    /// actually differs per database needs setting; the schema and pool sizes are usually shared.
    /// </param>
    public PostgresShardBuilder AddShard(
        string shardId,
        Func<PostgresOptions, PostgresOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        ArgumentNullException.ThrowIfNull(configure);

        var options = configure(_template)
            ?? throw new InvalidOperationException($"AddShard(\"{shardId}\", ...) configurator returned null.");

        // A malformed or duplicate id is reported as ALB0012 rather than thrown here: validation
        // collects every problem in the module and presents them together at startup.
        _shards.Add(new ShardDeclaration(shardId, new PostgresBackendDescriptor(options)));
        return this;
    }

    /// <summary>
    /// Declares the control database holding the tenant → shard mapping.
    /// </summary>
    /// <param name="configure">
    /// Transforms the module's own options into the catalog's. The catalog holds one small table
    /// and is read once per unknown tenant, so it wants a modest pool, not the module's.
    /// </param>
    /// <remarks>
    /// Deliberately a separate database from any shard: every request to this module resolves
    /// through the catalog, and putting it in a shard would make that shard load-bearing for
    /// routing to all the others.
    /// </remarks>
    public PostgresShardBuilder WithCatalog(Func<PostgresOptions, PostgresOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = configure(_template)
            ?? throw new InvalidOperationException("WithCatalog configurator returned null.");

        _catalog = new PostgresBackendDescriptor(options);
        return this;
    }

    /// <summary>
    /// Sets the shard a tenant is placed in the first time it is seen with no catalog entry.
    /// </summary>
    /// <remarks>
    /// Without this, an unmapped tenant is an <see cref="Tenancy.UnknownTenantException"/> — the
    /// right answer when tenant onboarding is a deliberate act. With it, tenants land wherever
    /// this points until an operator moves them.
    /// </remarks>
    public PostgresShardBuilder WithDefaultShard(string shardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        _defaultShardId = shardId;
        return this;
    }

    /// <summary>
    /// Sets how often the resolver re-reads the whole catalog. Default 30 seconds.
    /// </summary>
    /// <remarks>
    /// This is not how quickly a new tenant is picked up — a cache miss reads the catalog straight
    /// away. It is how quickly a mapping <em>changed</em> by another process is noticed.
    /// </remarks>
    public PostgresShardBuilder WithRefreshInterval(TimeSpan interval)
    {
        _refreshInterval = interval;
        return this;
    }

    internal TenancyDefinition ApplyTo(TenancyDefinition definition)
    {
        var result = definition with
        {
            Shards = definition.Shards.AddRange(_shards),
            Catalog = _catalog ?? definition.Catalog,
            DefaultShardId = _defaultShardId ?? definition.DefaultShardId,
        };

        return _refreshInterval is { } interval
            ? result with { CatalogRefreshInterval = interval }
            : result;
    }
}
