using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class PostgresDescriptorTests
{
    private const string ConnectionString = "Host=localhost;Database=alberto;Username=x;Password=y";

    private static AlbertoModuleDefinition Resolve(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

    private static PostgresOptions OptionsOf(AlbertoModuleDefinition definition) =>
        definition.Backend.Should().BeOfType<PostgresBackendDescriptor>().Subject.Options;

    [Fact]
    public void WithPostgres_declares_the_backend_without_connecting()
    {
        var services = new ServiceCollection();

        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString, Schema = "orders" }));

        var options = OptionsOf(Resolve(services));
        options.ConnectionString.Should().Be(ConnectionString);
        options.Schema.Should().Be("orders");
    }

    [Fact]
    public void Tenancy_declared_after_the_backend_still_reaches_the_backend()
    {
        var services = new ServiceCollection();

        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString })
            .WithTenancy());

        Resolve(services).TenancyEnabled.Should().BeTrue();
    }

    [Fact]
    public void Postgres_options_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Postgres:MaxPoolSize"] = "77",
                ["Alberto:Modules:orders:Postgres:AutoMigrate"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString, MaxPoolSize = 30 }));

        var options = OptionsOf(Resolve(services));
        options.MaxPoolSize.Should().Be(77);
        options.AutoMigrate.Should().BeFalse();
    }

    [Fact]
    public void A_connection_string_supplied_only_by_configuration_is_accepted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Postgres:ConnectionString"] = ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module.WithPostgres(o => o));

        new AlbertoModuleValidator()
            .Collect(Resolve(services))
            .Should().NotContain(f => f.Code == "ALB1001");
    }

    [Fact]
    public void An_empty_connection_string_fails_with_ALB1001()
    {
        // Note: AlbertoModuleValidator is registered as IValidateOptions<AlbertoModuleDefinition>
        // and runs inline when IOptionsMonitor.Get() is called, so we cannot resolve the definition
        // through the options machinery when it is invalid (it throws OptionsValidationException).
        // We construct the definition directly to isolate the backend validation logic.
        var descriptor = new PostgresBackendDescriptor(new PostgresOptions());
        var definition = new AlbertoModuleDefinition { ModuleKey = "orders", Backend = descriptor };

        new AlbertoModuleValidator()
            .Collect(definition)
            .Should().Contain(f => f.Code == "ALB1001");
    }

    [Fact]
    public void An_inverted_pool_range_fails_with_ALB1003()
    {
        // Same reasoning as An_empty_connection_string_fails_with_ALB1001 — construct directly.
        var descriptor = new PostgresBackendDescriptor(new PostgresOptions
        {
            ConnectionString = ConnectionString,
            MinPoolSize = 50,
            MaxPoolSize = 10,
        });
        var definition = new AlbertoModuleDefinition { ModuleKey = "orders", Backend = descriptor };

        new AlbertoModuleValidator()
            .Collect(definition)
            .Should().Contain(f => f.Code == "ALB1003");
    }

    // ── Finding I2 ──────────────────────────────────────────────────────────

    [Fact]
    public void Config_supplied_LeaseDuration_reaches_IProcessorLeaseManager()
    {
        // PostgresProcessorLeaseManager.LeaseDuration is public, so this test can verify
        // that the config-overlaid value is used without a real database connection.
        //
        // Before the fix factory lambdas captured the pre-configuration Options instance,
        // so they would see LeaseDuration = 10 s even when config said 3 min.
        // After the fix they resolve PostgresOptions from IOptionsMonitor at service-resolution
        // time, so the config-overlaid value wins.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Postgres:LeaseDuration"] = "00:03:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with
            {
                ConnectionString = ConnectionString,
                LeaseDuration = TimeSpan.FromSeconds(10), // must be superseded by config
            }));

        using var provider = services.BuildServiceProvider();
        var leaseManager = provider.GetRequiredKeyedService<IProcessorLeaseManager>("orders");

        leaseManager.LeaseDuration.Should().Be(TimeSpan.FromMinutes(3),
            "LeaseDuration from configuration (3 min) must supersede the code value (10 s)");
    }

    [Fact]
    public async Task A_failed_migration_prevents_host_start_and_names_the_module()
    {
        // Use a port nothing listens on with a short timeout so the test completes quickly
        // without requiring Docker or a real Postgres instance.
        const string unreachable =
            "Host=127.0.0.1;Port=19999;Database=alberto;Username=x;Password=y;Timeout=1";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = unreachable }));

        using var host = builder.Build();

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("orders");
        exception.Which.Message.Should().Contain("migration");
    }
}
