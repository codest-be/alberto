using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Postgres;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Tests that <see cref="DcbModuleBuilderExtensions.WithControlLoop"/> correctly records intent
/// into the <see cref="AlbertoModuleDefinition"/> and that configuration wins over code defaults.
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
            .WithInMemory()
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
        services.AddAlberto("orders", module => module.WithInMemory());

        Resolve(services, "orders").ControlLoop.Should().Be(ControlLoopOptions.Default);
    }

    [Fact]
    public void Retry_settings_are_reachable_through_the_control_loop_record()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithInMemory()
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
            .WithInMemory()
            .WithControlLoop(o => o with { BatchSize = 500, Retry = o.Retry with { MaxRetries = 5 } }));

        var loop = Resolve(services, "orders").ControlLoop;

        loop.BatchSize.Should().Be(7);
        loop.Retry.MaxRetries.Should().Be(11);
    }

    [Fact]
    public void Leases_are_declared_through_the_options_record()
    {
        // Postgres, not in-memory: the in-memory backend provides no IProcessorLeaseManager,
        // so enabling leases on it is rejected at startup by ALB0024. Nothing connects here —
        // PostgresBackendDescriptor.Validate only checks the connection string is present.
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = "Host=localhost;Database=alberto" })
            .WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true, ReplicaId = "pod-1" } }));

        var leases = Resolve(services, "orders").ControlLoop.Leases;

        leases.Enabled.Should().BeTrue();
        leases.ReplicaId.Should().Be("pod-1");
    }

    [Fact]
    public void EffectiveReplicaId_returns_machine_name_when_ReplicaId_is_null()
    {
        var options = new ProcessorLeaseOptions { ReplicaId = null };

        options.EffectiveReplicaId.Should().Be(Environment.MachineName);
    }

    [Fact]
    public void EffectiveReplicaId_returns_machine_name_when_ReplicaId_is_empty()
    {
        var options = new ProcessorLeaseOptions { ReplicaId = "" };

        options.EffectiveReplicaId.Should().Be(Environment.MachineName);
    }

    [Fact]
    public void EffectiveReplicaId_returns_machine_name_when_ReplicaId_is_whitespace()
    {
        var options = new ProcessorLeaseOptions { ReplicaId = "   " };

        options.EffectiveReplicaId.Should().Be(Environment.MachineName);
    }

    [Fact]
    public void EffectiveReplicaId_returns_the_value_verbatim_when_set()
    {
        var options = new ProcessorLeaseOptions { ReplicaId = "pod-1" };

        options.EffectiveReplicaId.Should().Be("pod-1");
    }

    [Fact]
    public void EffectiveReplicaId_does_not_trim_surrounding_content()
    {
        // The guard is IsNullOrWhiteSpace, not a trim: a value with leading or trailing
        // spaces is non-whitespace and is returned exactly as supplied.
        var options = new ProcessorLeaseOptions { ReplicaId = " pod-1 " };

        options.EffectiveReplicaId.Should().Be(" pod-1 ");
    }

}
