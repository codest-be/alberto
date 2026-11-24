using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Configuration;

public class StartupValidationTests
{
    private static IHost BuildHost(Action<AlbertoModuleBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto("orders", configure);
        return builder.Build();
    }

    [Fact]
    public async Task A_module_without_a_backend_refuses_to_start()
    {
        using var host = BuildHost(_ => { });

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<OptionsValidationException>(act);
        Assert.Contains("ALB0001", exception.Message);
    }

    [Fact]
    public async Task The_failure_message_names_the_module_and_the_remedy()
    {
        using var host = BuildHost(_ => { });

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<OptionsValidationException>(act);
        Assert.Contains("Alberto module 'orders' cannot start", exception.Message);
        Assert.Contains("AddAlberto(\"orders\", ...)", exception.Message);
        Assert.Contains("Alberto:Modules:orders", exception.Message);
    }

    [Fact]
    public async Task WithInMemory_and_WithRebuilds_refuses_to_start()
    {
        using var host = BuildHost(b => b.WithInMemory().WithRebuilds());

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<OptionsValidationException>(act);
        Assert.Contains("ALB0023", exception.Message);
    }

    [Fact]
    public async Task WithInMemory_and_leases_enabled_starts()
    {
        // Formerly rejected with ALB0024. The in-memory backend now ships an
        // IProcessorLeaseManager and a fenced checkpoint store, so a lease-enabled module
        // runs the same control-loop topology it ships with on Postgres.
        using var host = BuildHost(b =>
            b.WithInMemory()
             .WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } }));

        var cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken);
        await host.StopAsync(cancellationToken);
    }
}
