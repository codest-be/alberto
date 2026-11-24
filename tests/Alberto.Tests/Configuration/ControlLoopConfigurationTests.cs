using System.Reflection;
using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Postgres;
using Alberto.Projections;
using Alberto.Subscriptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Tests that <see cref="AlbertoModuleBuilderExtensions.WithControlLoop"/> correctly records intent
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

        Assert.Equal(TimeSpan.FromMilliseconds(10), loop.PollingInterval);
        Assert.Equal(500, loop.BatchSize);
        // untouched properties keep their default
        Assert.Equal(2000, loop.HeadWindowSize);
    }

    [Fact]
    public void WithControlLoop_is_implied_when_it_is_never_called()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());

        Assert.Equal(ControlLoopOptions.Default, Resolve(services, "orders").ControlLoop);
    }

    [Fact]
    public void Retry_settings_are_reachable_through_the_control_loop_record()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with { Retry = o.Retry with { MaxRetries = 5 } }));

        Assert.Equal(5, Resolve(services, "orders").ControlLoop.Retry.MaxRetries);
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

        Assert.Equal(7, loop.BatchSize);
        Assert.Equal(11, loop.Retry.MaxRetries);
    }

    [Fact]
    public void Leases_are_declared_through_the_options_record()
    {
        // Postgres for variety — the in-memory backend also supports leases. Nothing connects
        // here: PostgresBackendDescriptor.Validate only checks the connection string is present.
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = "Host=localhost;Database=alberto" })
            .WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true, ReplicaId = "pod-1" } }));

        var leases = Resolve(services, "orders").ControlLoop.Leases;

        Assert.True(leases.Enabled);
        Assert.Equal("pod-1", leases.ReplicaId);
    }

    [Fact]
    public void EffectiveReplicaId_returns_machine_name_when_ReplicaId_is_null()
    {
        var options = new ProcessorLeaseOptions { ReplicaId = null };

        Assert.Equal(Environment.MachineName, options.EffectiveReplicaId);
    }

    [Fact]
    public void EffectiveReplicaId_returns_machine_name_when_ReplicaId_is_empty()
    {
        var options = new ProcessorLeaseOptions { ReplicaId = "" };

        Assert.Equal(Environment.MachineName, options.EffectiveReplicaId);
    }

    [Fact]
    public void EffectiveReplicaId_returns_machine_name_when_ReplicaId_is_whitespace()
    {
        var options = new ProcessorLeaseOptions { ReplicaId = "   " };

        Assert.Equal(Environment.MachineName, options.EffectiveReplicaId);
    }

    [Fact]
    public void EffectiveReplicaId_returns_the_value_verbatim_when_set()
    {
        var options = new ProcessorLeaseOptions { ReplicaId = "pod-1" };

        Assert.Equal("pod-1", options.EffectiveReplicaId);
    }

    [Fact]
    public void EffectiveReplicaId_does_not_trim_surrounding_content()
    {
        // The guard is IsNullOrWhiteSpace, not a trim: a value with leading or trailing
        // spaces is non-whitespace and is returned exactly as supplied.
        var options = new ProcessorLeaseOptions { ReplicaId = " pod-1 " };

        Assert.Equal(" pod-1 ", options.EffectiveReplicaId);
    }

    /// <summary>
    /// Kills L69 Survived Arithmetic mutation — options.PollingInterval / 3 instead of * 3.
    /// The default timeout for ProjectionCatchUp is Max(5s, pollingInterval * 3). With a
    /// PollingInterval large enough that pollingInterval * 3 > 5s, the two computations produce
    /// different results: the original gives pollingInterval * 3, the mutant gives 5s.
    /// </summary>
    [Fact]
    public async Task ProjectionCatchUp_default_timeout_is_three_polling_intervals_when_that_exceeds_five_seconds()
    {
        // PollingInterval = 2s → defaultTimeout = Max(5s, 2s × 3) = Max(5s, 6s) = 6s.
        // The arithmetic mutant (÷ 3) gives Max(5s, 0.667s) = 5s, which fails the assertion.
        var pollingInterval = TimeSpan.FromSeconds(2);

        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with { PollingInterval = pollingInterval }));

        // The checkpoint store the in-memory backend registers is IAsyncDisposable-only.
        await using var sp = services.BuildServiceProvider();
        var catchUp = sp.GetRequiredKeyedService<ProjectionCatchUp>("orders");

        var defaultTimeout = (TimeSpan)typeof(ProjectionCatchUp)
            .GetField("_defaultTimeout", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(catchUp)!;

        // the default timeout is 3× the polling interval when that exceeds the 5-second floor
        Assert.Equal(pollingInterval * 3, defaultTimeout);
    }

    [Fact]
    public async Task ProjectionCatchUp_default_timeout_floors_at_five_seconds_for_short_polling_intervals()
    {
        // PollingInterval = 250ms → Max(5s, 0.75s) = 5s (the floor applies).
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(250) }));

        // The checkpoint store the in-memory backend registers is IAsyncDisposable-only.
        await using var sp = services.BuildServiceProvider();
        var catchUp = sp.GetRequiredKeyedService<ProjectionCatchUp>("orders");

        var defaultTimeout = (TimeSpan)typeof(ProjectionCatchUp)
            .GetField("_defaultTimeout", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(catchUp)!;

        // the 5-second floor applies when 3× the polling interval is shorter
        Assert.Equal(TimeSpan.FromSeconds(5), defaultTimeout);
    }

}
