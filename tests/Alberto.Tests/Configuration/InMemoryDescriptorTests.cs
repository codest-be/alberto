using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Configuration;

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

        Assert.IsType<InMemoryBackendDescriptor>(Resolve(services).Backend);
    }

    [Fact]
    public void WithInMemory_satisfies_the_backend_requirement()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());

        Assert.DoesNotContain(new AlbertoModuleValidator()
            .Collect(Resolve(services)), f => f.Code == "ALB0001");
    }

    [Fact]
    public void The_in_memory_backend_supports_tenancy()
    {
        // InMemoryBackendDescriptor.SupportsTenancy was changed to true when the tenant-aware
        // path (InMemoryTenantEventStoreDecorator) was introduced. ALB0003 must no longer be
        // emitted for in-memory + tenancy combinations.
        // Pattern from ProcessorRegistrationTests.Two_reactors for capturing the definition
        // before IValidateOptions can throw.
        AlbertoModuleDefinition? captured = null;
        var services = new ServiceCollection();
        services.AddAlberto("orders", module =>
        {
            module.WithInMemory().WithTenancy();
            captured = module.Definition;
        });

        Assert.DoesNotContain(new AlbertoModuleValidator()
            .Collect(captured!), f => f.Code == "ALB0003");
    }

    [Fact]
    public async Task An_in_memory_tenant_module_starts_and_stops_cleanly()
    {
        // Regression guard: the tenant DI registration path (RegisterTenantBackend) must not
        // leave any unresolvable keyed services that cause startup to fail.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto("orders", module => module.WithInMemory().WithTenancy());
        using var host = builder.Build();

        var act = async () =>
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        };

        await act();
    }

    [Fact]
    public void A_shared_module_key_is_recorded_on_the_descriptor()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory("shared"));

        Assert.Equal("shared", Assert.IsType<InMemoryBackendDescriptor>(Resolve(services).Backend).SharedModuleKey);
    }

    [Fact]
    public void WithInMemory_shared_and_WithTenancy_emits_ALB0017()
    {
        // A shared backend is a singleton borrowed from another module; the tenant path needs a
        // scoped service. The combination cannot work, so the validator must reject it loudly
        // rather than letting the module start and silently write TenantId = null.
        AlbertoModuleDefinition? captured = null;
        var services = new ServiceCollection();
        services.AddAlberto("orders", module =>
        {
            module.WithInMemory("shared").WithTenancy();
            captured = module.Definition;
        });

        Assert.Single(new AlbertoModuleValidator()
            .Collect(captured!), f => f.Code == "ALB0017");
    }

    [Fact]
    public void WithInMemory_shared_without_tenancy_does_not_emit_ALB0017()
    {
        // The non-tenant shared path is the legitimate use case (spanning two modules in a test);
        // confirm the validator does not reject it.
        AlbertoModuleDefinition? captured = null;
        var services = new ServiceCollection();
        services.AddAlberto("orders", module =>
        {
            module.WithInMemory("shared");
            captured = module.Definition;
        });

        Assert.DoesNotContain(new AlbertoModuleValidator()
            .Collect(captured!), f => f.Code == "ALB0017");
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

        await act();
        Assert.NotNull(host.Services.GetRequiredKeyedService<IEventStore>("orders"));
    }

    [Fact]
    public void WithInMemory_rejects_a_null_builder_or_a_blank_shared_module_key()
    {
        var exception = Assert.ThrowsAny<ArgumentNullException>(() => InMemoryBuilderExtensions.WithInMemory(null!));
        Assert.Equal("builder", exception.ParamName);
        var exception2 = Assert.ThrowsAny<ArgumentNullException>(() => InMemoryBuilderExtensions.WithInMemory(null!, "orders"));
        Assert.Equal("builder", exception2.ParamName);
        var exception3 = Assert.ThrowsAny<ArgumentException>(() => new AlbertoModuleBuilder("payments").WithInMemory(" "));
        Assert.Equal("sharedModuleKey", exception3.ParamName);
    }
}
