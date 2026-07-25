using Alberto.Dcb.Postgres;
using Alberto.Dcb.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Alberto.Dcb.Tests.Tenancy;

/// <summary>
/// Two event-store databases and one control database on the shared cluster: the smallest
/// arrangement in which "tenant A is in db1 and tenant B is in db2" is a real statement about two
/// PostgreSQL databases rather than about two rows.
/// </summary>
/// <remarks>
/// Three clones of existing templates, no extra container. The shards are cloned from the
/// multi-tenant template because a shard is still row-level multi-tenant inside itself.
/// </remarks>
public sealed class ShardedPostgresFixture(PostgresCluster cluster) : IAsyncLifetime
{
    internal const string ModuleKey = "sharded-test";

    /// <summary>Tenants of db1 and db2. Assigned in <see cref="InitializeAsync"/>.</summary>
    internal const string TenantOnDb1 = "acme";
    internal const string TenantAlsoOnDb1 = "acmesub";
    internal const string TenantOnDb2 = "globex";

    public string Db1ConnectionString { get; private set; } = string.Empty;

    public string Db2ConnectionString { get; private set; } = string.Empty;

    public string CatalogConnectionString { get; private set; } = string.Empty;

    public NpgsqlDataSource Db1 { get; private set; } = null!;

    public NpgsqlDataSource Db2 { get; private set; } = null!;

    public NpgsqlDataSource Catalog { get; private set; } = null!;

    public IServiceProvider Services { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Db1ConnectionString = await cluster.CloneAsync(PostgresTemplates.MultiTenant, "shardeddb1");
        Db2ConnectionString = await cluster.CloneAsync(PostgresTemplates.MultiTenant, "shardeddb2");
        CatalogConnectionString = await cluster.CloneAsync(PostgresTemplates.Empty, "shardedcatalog");

        Db1 = NpgsqlDataSource.Create(Db1ConnectionString);
        Db2 = NpgsqlDataSource.Create(Db2ConnectionString);
        Catalog = NpgsqlDataSource.Create(CatalogConnectionString);

        // The hosted service that does this at startup is exercised separately; the fixture runs
        // the migration itself so the tests below need no host.
        var migration = PostgresCatalogMigrator.Migrate(CatalogConnectionString);
        if (!migration.Successful)
            throw new InvalidOperationException("Catalog migration failed.", migration.Error);

        Services = BuildServiceProvider();

        var map = Services.GetRequiredKeyedService<Dcb.Tenancy.ITenantShardMap>(ModuleKey);
        await map.AssignAsync(TenantOnDb1, "db1");
        await map.AssignAsync(TenantAlsoOnDb1, "db1");
        await map.AssignAsync(TenantOnDb2, "db2");
    }

    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, module => module
            .WithPostgres(o => o with
            {
                AutoMigrate = false,          // the templates are already migrated
                EnableNotifyListener = false, // no background postgres listener in tests
                MaxPoolSize = 5,              // two shards plus a catalog on a shared postmaster
            })
            .WithTenancy(t => t.AcrossPostgresDatabases(s => s
                .WithCatalog(o => o with { ConnectionString = CatalogConnectionString })
                .AddShard("db1", o => o with { ConnectionString = Db1ConnectionString })
                .AddShard("db2", o => o with { ConnectionString = Db2ConnectionString }))));

        return services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (Services is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        if (Db1 is not null)
            await Db1.DisposeAsync();
        if (Db2 is not null)
            await Db2.DisposeAsync();
        if (Catalog is not null)
            await Catalog.DisposeAsync();
    }
}
