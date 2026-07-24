using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Postgres;
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
