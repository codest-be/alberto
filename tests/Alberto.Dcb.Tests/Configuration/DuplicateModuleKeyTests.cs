using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

/// <summary>
/// Pins ALB0026: a module key is an identity, so <c>AddAlberto</c> refuses to register one twice.
/// </summary>
/// <remarks>
/// Registering the same key twice is silently destructive rather than additive — the second call
/// adds another <c>Configure</c> callback to the same named options (so the later declaration
/// overlays the earlier one) and another set of control loops that poll the same log and race on
/// one checkpoint row. Neither symptom is visible at registration time, which is exactly why the
/// guard has to be here.
/// </remarks>
public class DuplicateModuleKeyTests
{
    [Fact]
    public void A_second_AddAlberto_with_the_same_module_key_throws_ALB0026()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", m => m.WithInMemory());

        var act = () => services.AddAlberto("orders", m => m.WithInMemory());

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("ALB0026");
        exception.Message.Should().Contain("'orders'",
            "the error must name the key that was registered twice");
    }

    [Fact]
    public void Distinct_module_keys_are_unaffected()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", m => m.WithInMemory());

        var act = () => services.AddAlberto("payments", m => m.WithInMemory());

        act.Should().NotThrow("the guard is per key, not a one-module-per-collection rule");
    }

    [Fact]
    public void A_prefix_of_a_registered_key_is_not_treated_as_a_duplicate()
    {
        // The marker is compared for equality, not by prefix — "order" and "orders" are two
        // modules, and a StartsWith-style check would reject the second one.
        var services = new ServiceCollection();
        services.AddAlberto("orders", m => m.WithInMemory());

        var act = () => services.AddAlberto("order", m => m.WithInMemory());

        act.Should().NotThrow();
    }

    [Fact]
    public void The_rejected_call_registers_nothing()
    {
        // The guard runs before phase 1, so the first module must survive the second call
        // untouched. A guard placed after registration would leave the collection holding half
        // of a second module — worse than the duplicate it was meant to prevent.
        var services = new ServiceCollection();
        services.AddAlberto("orders", m => m
            .WithInMemory()
            .WithControlLoop(o => o with { BatchSize = 250 }));

        var descriptorsAfterFirstCall = services.Count;

        var act = () => services.AddAlberto("orders", m => m
            .WithInMemory()
            .WithControlLoop(o => o with { BatchSize = 999 }));

        act.Should().Throw<InvalidOperationException>();
        services.Count.Should().Be(descriptorsAfterFirstCall,
            "the second call must not have registered anything before throwing");

        var definition = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

        definition.ControlLoop.BatchSize.Should().Be(250,
            "the first module's declaration must be exactly what it was");
    }

    [Fact]
    public void The_guard_tolerates_keyed_descriptors_registered_by_the_rest_of_the_application()
    {
        // The duplicate scan walks every descriptor in the collection. Reading
        // ImplementationInstance or ImplementationType off a keyed descriptor throws, so a scan
        // written that way would fail on any application that uses keyed DI for its own services
        // — which, since Alberto itself registers keyed services, is every application using it.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<string>("some-other-key", "a value");
        services.AddKeyedSingleton<object>("orders", new object());

        var act = () => services.AddAlberto("orders", m => m.WithInMemory());

        act.Should().NotThrow("only Alberto's own marker identifies a registered module key");
    }
}
