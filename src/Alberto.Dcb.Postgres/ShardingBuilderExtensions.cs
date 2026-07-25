using Alberto.Dcb.Configuration;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Adds PostgreSQL database-per-shard tenancy to <see cref="TenancyBuilder"/>.
/// </summary>
public static class ShardingBuilderExtensions
{
    /// <summary>
    /// Spreads this module's tenants across several PostgreSQL databases. Row-level tenancy still
    /// applies <em>within</em> each one, so ten tenants can share <c>db1</c> while three others
    /// share <c>db2</c>.
    /// </summary>
    /// <param name="tenancy">The module's tenancy builder.</param>
    /// <param name="configure">Declares the shards, the catalog, and the placement policy.</param>
    /// <remarks>
    /// Each shard gets its own complete set of Alberto services — data source, migrations,
    /// checkpoints, control loops — under the service key <c>{moduleKey}#{shardId}</c>. Resolving
    /// <c>IEventStore</c> under the plain module key gives a router that forwards to whichever
    /// shard the current tenant belongs to, so calling code is unchanged.
    /// <para>
    /// Positions are per-database. Never compare a position from one shard with one from another.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddAlberto("orders", module => module
    ///     .WithPostgres(o => o with { Schema = "orders", MaxPoolSize = 30 })
    ///     .WithTenancy(t => t.AcrossPostgresDatabases(s => s
    ///         .WithCatalog(o => o with { ConnectionString = catalogCs })
    ///         .AddShard("db1", o => o with { ConnectionString = db1Cs })
    ///         .AddShard("db2", o => o with { ConnectionString = db2Cs, MaxPoolSize = 10 })
    ///         .WithDefaultShard("db1"))));
    /// </code>
    /// </example>
    public static TenancyBuilder AcrossPostgresDatabases(
        this TenancyBuilder tenancy,
        Action<PostgresShardBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(tenancy);
        ArgumentNullException.ThrowIfNull(configure);

        // A shard starts from the module's own .WithPostgres(...) so shared settings are written
        // once. A module that declared no backend, or a non-Postgres one, starts from defaults;
        // ALB0011 reports the mismatch.
        var template = tenancy.ModuleBackend is PostgresBackendDescriptor descriptor
            ? descriptor.Options
            : new PostgresOptions();

        var shards = new PostgresShardBuilder(template);
        configure(shards);

        return tenancy.Configure(shards.ApplyTo);
    }
}
