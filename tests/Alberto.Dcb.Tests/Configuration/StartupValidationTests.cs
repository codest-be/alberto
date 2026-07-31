using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class StartupValidationTests
{
    private static IHost BuildHost(Action<DcbModuleBuilder> configure)
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

        var exception = await act.Should().ThrowAsync<OptionsValidationException>();
        exception.Which.Message.Should().Contain("ALB0001");
    }

    [Fact]
    public async Task The_failure_message_names_the_module_and_the_remedy()
    {
        using var host = BuildHost(_ => { });

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<OptionsValidationException>();
        exception.Which.Message.Should().Contain("Alberto module 'orders' cannot start");
        exception.Which.Message.Should().Contain("AddAlberto(\"orders\", ...)");
        exception.Which.Message.Should().Contain("Alberto:Modules:orders");
    }

    [Fact]
    public async Task WithInMemory_and_WithRebuilds_refuses_to_start()
    {
        using var host = BuildHost(b => b.WithInMemory().WithRebuilds());

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<OptionsValidationException>();
        exception.Which.Message.Should().Contain("ALB0023");
    }

    [Fact]
    public async Task WithInMemory_and_leases_enabled_refuses_to_start()
    {
        using var host = BuildHost(b =>
            b.WithInMemory()
             .WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } }));

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<OptionsValidationException>();
        exception.Which.Message.Should().Contain("ALB0024");
    }
}
