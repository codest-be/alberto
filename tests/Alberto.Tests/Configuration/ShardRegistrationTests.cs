using Alberto.Configuration;
using Alberto.Postgres;
using Alberto.Checkpoints;
using Alberto.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// What <c>AcrossPostgresDatabases</c> produces: one complete set of services per shard, plus the
/// router and catalog under the logical module key. Nothing here opens a connection.
/// </summary>
public class ShardRegistrationTests
{
    private const string Db1 = "Host=db1;Database=alberto;Username=x;Password=y";
    private const string Db2 = "Host=db2;Database=alberto;Username=x;Password=y";
    private const string CatalogCs = "Host=control;Database=alberto_catalog;Username=x;Password=y";

    private static IServiceCollection ShardedModule(
        IConfiguration? configuration = null,
        Action<PostgresShardBuilder>? extra = null)
    {
        var services = new ServiceCollection();
        if (configuration is not null)
            services.AddSingleton(configuration);

        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { Schema = "orders", MaxPoolSize = 30 })
            .WithTenancy(t => t.AcrossPostgresDatabases(s =>
            {
                s.WithCatalog(o => o with { ConnectionString = CatalogCs })
                    .AddShard("db1", o => o with { ConnectionString = Db1 })
                    .AddShard("db2", o => o with { ConnectionString = Db2, MaxPoolSize = 10 })
                    .WithDefaultShard("db1");
                extra?.Invoke(s);
            })));

        return services;
    }

    private static AlbertoModuleDefinition Definition(IServiceCollection services, string name) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get(name);

    private static PostgresOptions PostgresOf(AlbertoModuleDefinition definition) =>
        Assert.IsType<PostgresBackendDescriptor>(definition.Backend).Options;

    [Fact]
    public void The_shards_are_declared_in_order()
    {
        var definition = Definition(ShardedModule(), "orders");

        Assert.True(definition.Tenancy.IsSharded);
        Assert.Equal(["db1", "db2"], definition.Tenancy.ShardIds);
        Assert.Equal("db1", definition.Tenancy.DefaultShardId);
    }

    [Fact]
    public void A_shard_inherits_the_modules_backend_settings_and_overrides_what_it_names()
    {
        var definition = Definition(ShardedModule(), "orders");
        var shards = definition.Tenancy.Shards
            .ToDictionary(s => s.ShardId, s => ((PostgresBackendDescriptor)s.Backend).Options);

        Assert.Equal(Db1, shards["db1"].ConnectionString);
        Assert.Equal("orders", shards["db1"].Schema);
        Assert.Equal(30, shards["db1"].MaxPoolSize);

        Assert.Equal(Db2, shards["db2"].ConnectionString);
        Assert.Equal("orders", shards["db2"].Schema);
        Assert.Equal(10, shards["db2"].MaxPoolSize);
    }

    [Fact]
    public void The_catalog_inherits_the_modules_settings_too()
    {
        var catalog = Assert.IsType<PostgresBackendDescriptor>(Definition(ShardedModule(), "orders").Tenancy.Catalog).Options;

        Assert.Equal(CatalogCs, catalog.ConnectionString);
        Assert.Equal("orders", catalog.Schema);
    }

    [Fact]
    public void Each_shard_gets_its_own_options_instance_under_its_own_key()
    {
        var services = ShardedModule();

        Assert.Equal(Db1, PostgresOf(Definition(services, "orders#db1")).ConnectionString);
        Assert.Equal(Db2, PostgresOf(Definition(services, "orders#db2")).ConnectionString);
    }

    [Fact]
    public void A_shards_own_definition_is_tenant_aware_but_not_itself_sharded()
    {
        // A shard is still row-level multi-tenant inside itself; dropping the shard list is what
        // keeps validation and registration from recursing.
        var shard = Definition(ShardedModule(), "orders#db1");

        Assert.True(shard.TenancyEnabled);
        Assert.False(shard.Tenancy.IsSharded);
        Assert.Equal("db1", shard.ShardId);
        Assert.Equal("orders", shard.ModuleKey);
        Assert.Equal("orders#db1", shard.ServiceKey);
    }

    [Fact]
    public void The_logical_key_resolves_to_the_router_and_the_shard_keys_to_real_stores()
    {
        var provider = ShardedModule().BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<ShardRoutingEventStore>(scope.ServiceProvider.GetRequiredKeyedService<IEventStore>("orders"));

        Assert.IsNotType<ShardRoutingEventStore>(scope.ServiceProvider.GetRequiredKeyedService<IEventStore>("orders#db1"));
        Assert.IsNotType<ShardRoutingEventStore>(scope.ServiceProvider.GetRequiredKeyedService<IEventStore>("orders#db2"));
    }

    [Fact]
    public void Each_shard_gets_its_own_checkpoint_store()
    {
        // Positions come from a per-database sequence, so a shared checkpoint store would compare
        // numbers from unrelated sequences.
        var provider = ShardedModule().BuildServiceProvider();

        var db1 = provider.GetRequiredKeyedService<ICheckpointStore>("orders#db1");
        var db2 = provider.GetRequiredKeyedService<ICheckpointStore>("orders#db2");

        Assert.NotSame(db2, db1);
    }

    [Fact]
    public void The_catalog_and_the_resolver_are_registered_under_the_logical_key()
    {
        var provider = ShardedModule().BuildServiceProvider();

        Assert.IsType<PostgresTenantShardMap>(provider.GetRequiredKeyedService<ITenantShardMap>("orders"));
        Assert.NotNull(provider.GetRequiredKeyedService<TenantShardResolver>("orders"));
        Assert.NotNull(provider.GetRequiredKeyedService<ShardHealth>("orders"));
    }

    [Fact]
    public void The_shard_health_check_joins_the_hosts_health_pipeline()
    {
        var provider = ShardedModule().BuildServiceProvider();

        Assert.Single(provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations, r => r.Name == "alberto-shards-orders");
    }

    [Fact]
    public void Configuration_tunes_a_declared_shard()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Tenancy:Shards:db2:MaxPoolSize"] = "50",
                ["Alberto:Modules:orders:Tenancy:DefaultShard"] = "db2",
                ["Alberto:Modules:orders:Tenancy:CatalogRefreshInterval"] = "00:01:00",
            })
            .Build();

        var definition = Definition(ShardedModule(configuration), "orders");

        var db2 = definition.Tenancy.Shards.Single(s => s.ShardId == "db2").Backend;
        Assert.Equal(50, ((PostgresBackendDescriptor)db2).Options.MaxPoolSize);
        Assert.Equal("db2", definition.Tenancy.DefaultShardId);
        Assert.Equal(TimeSpan.FromMinutes(1), definition.Tenancy.CatalogRefreshInterval);
    }

    [Fact]
    public void The_modules_own_configuration_section_reaches_every_shard()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Postgres:Schema"] = "orders_v2",
            })
            .Build();

        var definition = Definition(ShardedModule(configuration), "orders");

        Assert.All(definition.Tenancy.Shards
            .Select(s => ((PostgresBackendDescriptor)s.Backend).Options.Schema), item => Assert.Equal("orders_v2", item));
    }

    [Fact]
    public void A_shards_own_section_wins_over_the_modules()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Postgres:Schema"] = "orders_v2",
                ["Alberto:Modules:orders:Tenancy:Shards:db2:Schema"] = "orders_legacy",
            })
            .Build();

        var shards = Definition(ShardedModule(configuration), "orders").Tenancy.Shards
            .ToDictionary(s => s.ShardId, s => ((PostgresBackendDescriptor)s.Backend).Options.Schema);

        Assert.Equal("orders_v2", shards["db1"]);
        Assert.Equal("orders_legacy", shards["db2"]);
    }

    [Fact]
    public void Configuration_can_point_the_catalog_somewhere_else()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Tenancy:Catalog:ConnectionString"] =
                    "Host=control2;Database=alberto_catalog;Username=x;Password=y",
            })
            .Build();

        var catalog = (PostgresBackendDescriptor)Definition(ShardedModule(configuration), "orders").Tenancy.Catalog!;

        Assert.Contains("Host=control2", catalog.Options.ConnectionString);
    }

    [Fact]
    public void A_shard_that_exists_only_in_configuration_is_recorded_rather_than_created()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Tenancy:Shards:db3:ConnectionString"] =
                    "Host=db3;Database=alberto;Username=x;Password=y",
            })
            .Build();

        var services = ShardedModule(configuration);
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Registration ran while the collection was being built, before this configuration was
        // read, so the shard has no services of its own however complete its section looks.
        Assert.Null(scope.ServiceProvider.GetKeyedService<IEventStore>("orders#db3"));

        var resolve = () => Definition(services, "orders");
        var exception = Assert.ThrowsAny<OptionsValidationException>(resolve);
        Assert.Contains("ALB0015", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_malformed_shard_id_is_reported_by_validation_rather_than_thrown_at_declaration()
    {
        // Throwing at .AddShard(...) would hide every other problem behind the first one.
        var services = ShardedModule(extra: s => s.AddShard(
            "DB-THREE", o => o with { ConnectionString = "Host=db3;Database=alberto;Username=x;Password=y" }));

        var resolve = () => Definition(services, "orders");

        var exception = Assert.ThrowsAny<OptionsValidationException>(resolve);
        Assert.Contains("ALB0012", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
