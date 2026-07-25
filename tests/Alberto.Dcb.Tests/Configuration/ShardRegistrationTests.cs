using Alberto.Dcb.Configuration;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

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
        definition.Backend.Should().BeOfType<PostgresBackendDescriptor>().Subject.Options;

    [Fact]
    public void The_shards_are_declared_in_order()
    {
        var definition = Definition(ShardedModule(), "orders");

        definition.Tenancy.IsSharded.Should().BeTrue();
        definition.Tenancy.ShardIds.Should().Equal("db1", "db2");
        definition.Tenancy.DefaultShardId.Should().Be("db1");
    }

    [Fact]
    public void A_shard_inherits_the_modules_backend_settings_and_overrides_what_it_names()
    {
        var definition = Definition(ShardedModule(), "orders");
        var shards = definition.Tenancy.Shards
            .ToDictionary(s => s.ShardId, s => ((PostgresBackendDescriptor)s.Backend).Options);

        shards["db1"].ConnectionString.Should().Be(Db1);
        shards["db1"].Schema.Should().Be("orders");
        shards["db1"].MaxPoolSize.Should().Be(30);

        shards["db2"].ConnectionString.Should().Be(Db2);
        shards["db2"].Schema.Should().Be("orders");
        shards["db2"].MaxPoolSize.Should().Be(10);
    }

    [Fact]
    public void The_catalog_inherits_the_modules_settings_too()
    {
        var catalog = Definition(ShardedModule(), "orders").Tenancy.Catalog
            .Should().BeOfType<PostgresBackendDescriptor>().Subject.Options;

        catalog.ConnectionString.Should().Be(CatalogCs);
        catalog.Schema.Should().Be("orders");
    }

    [Fact]
    public void Each_shard_gets_its_own_options_instance_under_its_own_key()
    {
        var services = ShardedModule();

        PostgresOf(Definition(services, "orders#db1")).ConnectionString.Should().Be(Db1);
        PostgresOf(Definition(services, "orders#db2")).ConnectionString.Should().Be(Db2);
    }

    [Fact]
    public void A_shards_own_definition_is_tenant_aware_but_not_itself_sharded()
    {
        // A shard is still row-level multi-tenant inside itself; dropping the shard list is what
        // keeps validation and registration from recursing.
        var shard = Definition(ShardedModule(), "orders#db1");

        shard.TenancyEnabled.Should().BeTrue();
        shard.Tenancy.IsSharded.Should().BeFalse();
        shard.ShardId.Should().Be("db1");
        shard.ModuleKey.Should().Be("orders");
        shard.ServiceKey.Should().Be("orders#db1");
    }

    [Fact]
    public void The_logical_key_resolves_to_the_router_and_the_shard_keys_to_real_stores()
    {
        var provider = ShardedModule().BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredKeyedService<IEventStore>("orders")
            .Should().BeOfType<ShardRoutingEventStore>();
        scope.ServiceProvider.GetRequiredKeyedService<IEventStoreBackend>("orders")
            .Should().BeOfType<ShardRoutingEventStoreBackend>();

        scope.ServiceProvider.GetRequiredKeyedService<IEventStore>("orders#db1")
            .Should().NotBeOfType<ShardRoutingEventStore>();
        scope.ServiceProvider.GetRequiredKeyedService<IEventStore>("orders#db2")
            .Should().NotBeOfType<ShardRoutingEventStore>();
    }

    [Fact]
    public void Each_shard_gets_its_own_checkpoint_store()
    {
        // Positions come from a per-database sequence, so a shared checkpoint store would compare
        // numbers from unrelated sequences.
        var provider = ShardedModule().BuildServiceProvider();

        var db1 = provider.GetRequiredKeyedService<ICheckpointStore>("orders#db1");
        var db2 = provider.GetRequiredKeyedService<ICheckpointStore>("orders#db2");

        db1.Should().NotBeSameAs(db2);
    }

    [Fact]
    public void The_catalog_and_the_resolver_are_registered_under_the_logical_key()
    {
        var provider = ShardedModule().BuildServiceProvider();

        provider.GetRequiredKeyedService<ITenantShardMap>("orders")
            .Should().BeOfType<PostgresTenantShardMap>();
        provider.GetRequiredKeyedService<TenantShardResolver>("orders").Should().NotBeNull();
        provider.GetRequiredKeyedService<ShardHealth>("orders").Should().NotBeNull();
    }

    [Fact]
    public void The_shard_health_check_joins_the_hosts_health_pipeline()
    {
        var provider = ShardedModule().BuildServiceProvider();

        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Should().ContainSingle(r => r.Name == "alberto-shards-orders");
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
        ((PostgresBackendDescriptor)db2).Options.MaxPoolSize.Should().Be(50);
        definition.Tenancy.DefaultShardId.Should().Be("db2");
        definition.Tenancy.CatalogRefreshInterval.Should().Be(TimeSpan.FromMinutes(1));
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

        definition.Tenancy.Shards
            .Select(s => ((PostgresBackendDescriptor)s.Backend).Options.Schema)
            .Should().AllBe("orders_v2");
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

        shards["db1"].Should().Be("orders_v2");
        shards["db2"].Should().Be("orders_legacy");
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

        catalog.Options.ConnectionString.Should().Contain("Host=control2");
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
        scope.ServiceProvider.GetKeyedService<IEventStore>("orders#db3").Should().BeNull();

        var resolve = () => Definition(services, "orders");
        resolve.Should().Throw<OptionsValidationException>().WithMessage("*ALB0015*");
    }

    [Fact]
    public void A_malformed_shard_id_is_reported_by_validation_rather_than_thrown_at_declaration()
    {
        // Throwing at .AddShard(...) would hide every other problem behind the first one.
        var services = ShardedModule(extra: s => s.AddShard(
            "DB-THREE", o => o with { ConnectionString = "Host=db3;Database=alberto;Username=x;Password=y" }));

        var resolve = () => Definition(services, "orders");

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*ALB0012*");
    }
}
