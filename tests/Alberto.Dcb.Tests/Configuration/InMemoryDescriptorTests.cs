using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class InMemoryDescriptorTests
{
    private static AlbertoModuleDefinition Resolve(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

    [Fact]
    public void WithInMemory_declares_the_backend()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());

        Resolve(services).Backend.Should().BeOfType<InMemoryBackendDescriptor>();
    }

    [Fact]
    public void WithInMemory_satisfies_the_backend_requirement()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());

        new AlbertoModuleValidator()
            .Collect(Resolve(services))
            .Should().NotContain(f => f.Code == "ALB0001");
    }

    [Fact]
    public void The_in_memory_backend_does_not_support_tenancy()
    {
        // Capture the definition before the options monitor runs IValidateOptions (which throws
        // on failure). Pattern established by ProcessorRegistrationTests.Two_reactors.
        AlbertoModuleDefinition? captured = null;
        var services = new ServiceCollection();
        services.AddAlberto("orders", module =>
        {
            module.WithInMemory().WithTenancy();
            captured = module.Definition;
        });

        new AlbertoModuleValidator()
            .Collect(captured!)
            .Should().Contain(f => f.Code == "ALB0003");
    }

    [Fact]
    public void A_shared_module_key_is_recorded_on_the_descriptor()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory("shared"));

        Resolve(services).Backend.Should().BeOfType<InMemoryBackendDescriptor>()
            .Which.SharedModuleKey.Should().Be("shared");
    }

    [Fact]
    public async Task An_in_memory_module_starts_and_stops_cleanly()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto("orders", module => module.WithInMemory());
        using var host = builder.Build();

        var act = async () =>
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        };

        await act.Should().NotThrowAsync();
        host.Services.GetRequiredKeyedService<IEventStore>("orders").Should().NotBeNull();
    }
}
