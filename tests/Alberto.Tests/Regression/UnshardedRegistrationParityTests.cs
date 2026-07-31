using Alberto.Configuration;
using Alberto.Postgres;
using Alberto.Tenancy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Regression;

/// <summary>
/// Sharding is opt-in, and a module that does not opt in must be registered exactly as it was
/// before sharding existed: no router in front of its event store, no catalog, no shard services.
/// </summary>
public class UnshardedRegistrationParityTests
{
    private const string ConnectionString = "Host=localhost;Database=alberto;Username=x;Password=y";

    private static ServiceProvider Provider(Action<DcbModuleBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", configure);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider SingleTenant() =>
        Provider(module => module.WithPostgres(o => o with { ConnectionString = ConnectionString }));

    private static ServiceProvider RowLevelMultiTenant() =>
        Provider(module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString })
            .WithTenancy());

    [Fact]
    public void A_single_tenant_module_gets_no_router()
    {
        using var provider = SingleTenant();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredKeyedService<IEventStore>("orders")
            .Should().NotBeOfType<ShardRoutingEventStore>();
        scope.ServiceProvider.GetRequiredKeyedService<IEventStoreBackend>("orders")
            .Should().NotBeOfType<ShardRoutingEventStoreBackend>();
    }

    [Fact]
    public void A_row_level_multi_tenant_module_gets_no_router_either()
    {
        // .WithTenancy() on its own still means one database with a tenant_id column — the
        // standard, and unchanged.
        using var provider = RowLevelMultiTenant();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredKeyedService<IEventStore>("orders")
            .Should().NotBeOfType<ShardRoutingEventStore>();
    }

    [Fact]
    public void An_unsharded_module_registers_no_shard_services()
    {
        using var provider = RowLevelMultiTenant();

        provider.GetKeyedService<ITenantShardMap>("orders").Should().BeNull();
        provider.GetKeyedService<TenantShardResolver>("orders").Should().BeNull();
        provider.GetKeyedService<ShardHealth>("orders").Should().BeNull();
    }

    [Fact]
    public void An_unsharded_module_has_no_shard_in_its_definition_or_its_key()
    {
        using var provider = RowLevelMultiTenant();

        var definition = provider.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get("orders");

        definition.ShardId.Should().BeNull();
        definition.ServiceKey.Should().Be("orders");
        definition.DisplayName.Should().Be("orders");
        definition.Tenancy.IsSharded.Should().BeFalse();
        definition.Tenancy.Shards.Should().BeEmpty();
        definition.Tenancy.Catalog.Should().BeNull();
    }

    [Fact]
    public void An_unsharded_module_registers_exactly_one_options_instance()
    {
        // A sharded module adds one named instance per shard. An unsharded one must not.
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString })
            .WithTenancy());

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>();

        monitor.Get("orders").ModuleKey.Should().Be("orders");

        // Nothing configured a shard-keyed instance, so what comes back is an empty definition
        // that fails its own validation rather than a copy of the module's.
        var shardInstance = () => monitor.Get("orders#db1");
        shardInstance.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Health_checks_are_registered_only_for_sharded_modules()
    {
        using var unsharded = RowLevelMultiTenant();

        unsharded.GetRequiredService<IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>()
            .Value.Registrations.Should().NotContain(r => r.Name.StartsWith("alberto-shards", StringComparison.Ordinal));
    }
}
