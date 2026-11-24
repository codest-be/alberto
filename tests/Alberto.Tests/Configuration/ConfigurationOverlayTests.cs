using Alberto.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alberto.Tests.Configuration;

public class ConfigurationOverlayTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static AlbertoModuleDefinition Definition() => new()
    {
        ModuleKey = "orders",
        ControlLoop = new ControlLoopOptions { BatchSize = 500 },
    };

    [Fact]
    public void Configuration_overrides_the_code_default()
    {
        var configuration = Configuration(("Alberto:Modules:orders:ControlLoop:BatchSize", "42"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        Assert.Equal(42, result.ControlLoop.BatchSize);
    }

    [Fact]
    public void Absent_configuration_leaves_the_code_default_intact()
    {
        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), Configuration());

        Assert.Equal(500, result.ControlLoop.BatchSize);
    }

    [Fact]
    public void A_present_section_only_overrides_the_keys_it_names()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:ControlLoop:PollingInterval", "00:00:00.050"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        Assert.Equal(TimeSpan.FromMilliseconds(50), result.ControlLoop.PollingInterval);
        Assert.Equal(500, result.ControlLoop.BatchSize);
    }

    [Fact]
    public void Nested_sections_bind()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:ControlLoop:Retry:MaxRetries", "7"),
            ("Alberto:Modules:orders:ControlLoop:Leases:Enabled", "true"),
            ("Alberto:Modules:orders:ControlLoop:Leases:ReplicaId", "pod-3"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        Assert.Equal(7, result.ControlLoop.Retry.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(1), result.ControlLoop.Retry.RetryDelay);
        Assert.True(result.ControlLoop.Leases.Enabled);
        Assert.Equal("pod-3", result.ControlLoop.Leases.ReplicaId);
    }

    [Fact]
    public void Another_modules_section_is_ignored()
    {
        var configuration = Configuration(("Alberto:Modules:billing:ControlLoop:BatchSize", "1"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        Assert.Equal(500, result.ControlLoop.BatchSize);
    }

    [Fact]
    public void Telemetry_and_checkpoint_sections_bind()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:Telemetry:Enabled", "false"),
            ("Alberto:Modules:orders:Checkpoints:OrphanPolicy", "Strict"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        Assert.False(result.Telemetry.Enabled);
        Assert.Equal(OrphanCheckpointPolicy.Strict, result.Checkpoints.OrphanPolicy);
    }
}
