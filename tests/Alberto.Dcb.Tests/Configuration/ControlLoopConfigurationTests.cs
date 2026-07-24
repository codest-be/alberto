using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

/// <summary>
/// Tests that <see cref="DcbModuleBuilderExtensions.WithControlLoop"/> correctly records intent
/// into the <see cref="AlbertoModuleDefinition"/> and that configuration wins over code defaults.
///
/// NOTE: the brief specifies <c>.WithInMemory()</c> in these tests, but <c>WithInMemory()</c>
/// does not yet implement <c>IAlbertoBackendDescriptor</c> (that is Task 9). A <see cref="StubBackend"/>
/// is used instead so the validator (ALB0001) does not fail. Task 9 can update these to use
/// <c>.WithInMemory()</c> once the descriptor exists.
/// </summary>
public class ControlLoopConfigurationTests
{
    private static AlbertoModuleDefinition Resolve(IServiceCollection services, string moduleKey) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get(moduleKey);

    [Fact]
    public void WithControlLoop_transforms_the_options_record()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .UseBackend(new StubBackend())
            .WithControlLoop(o => o with
            {
                PollingInterval = TimeSpan.FromMilliseconds(10),
                BatchSize = 500,
            }));

        var loop = Resolve(services, "orders").ControlLoop;

        loop.PollingInterval.Should().Be(TimeSpan.FromMilliseconds(10));
        loop.BatchSize.Should().Be(500);
        loop.HeadWindowSize.Should().Be(2000, "untouched properties keep their default");
    }

    [Fact]
    public void WithControlLoop_is_implied_when_it_is_never_called()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.UseBackend(new StubBackend()));

        Resolve(services, "orders").ControlLoop.Should().Be(ControlLoopOptions.Default);
    }

    [Fact]
    public void Retry_settings_are_reachable_through_the_control_loop_record()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .UseBackend(new StubBackend())
            .WithControlLoop(o => o with { Retry = o.Retry with { MaxRetries = 5 } }));

        Resolve(services, "orders").ControlLoop.Retry.MaxRetries.Should().Be(5);
    }

    [Fact]
    public void Configuration_wins_over_WithControlLoop()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:ControlLoop:BatchSize"] = "7",
                ["Alberto:Modules:orders:ControlLoop:Retry:MaxRetries"] = "11",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module
            .UseBackend(new StubBackend())
            .WithControlLoop(o => o with { BatchSize = 500, Retry = o.Retry with { MaxRetries = 5 } }));

        var loop = Resolve(services, "orders").ControlLoop;

        loop.BatchSize.Should().Be(7);
        loop.Retry.MaxRetries.Should().Be(11);
    }

    [Fact]
    public void Leases_are_declared_through_the_options_record()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .UseBackend(new StubBackend())
            .WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true, ReplicaId = "pod-1" } }));

        var leases = Resolve(services, "orders").ControlLoop.Leases;

        leases.Enabled.Should().BeTrue();
        leases.ReplicaId.Should().Be("pod-1");
    }

    /// <summary>
    /// Minimal backend that satisfies the ALB0001 validator without registering any real services.
    /// Replace with <c>.WithInMemory()</c> once Task 9 creates the InMemory backend descriptor.
    /// </summary>
    private sealed class StubBackend : IAlbertoBackendDescriptor
    {
        public string Name => "Stub";
        public bool SupportsTenancy => false;

        public IAlbertoBackendDescriptor ApplyConfiguration(Microsoft.Extensions.Configuration.IConfiguration moduleSection)
            => this;

        public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition)
            => [];

        public void Register(AlbertoModuleContext context) { }
    }
}
