using Alberto.Configuration;
using FluentAssertions;
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

        result.ControlLoop.BatchSize.Should().Be(42);
    }

    [Fact]
    public void Absent_configuration_leaves_the_code_default_intact()
    {
        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), Configuration());

        result.ControlLoop.BatchSize.Should().Be(500);
    }

    [Fact]
    public void A_present_section_only_overrides_the_keys_it_names()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:ControlLoop:PollingInterval", "00:00:00.050"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.ControlLoop.PollingInterval.Should().Be(TimeSpan.FromMilliseconds(50));
        result.ControlLoop.BatchSize.Should().Be(500);
    }

    [Fact]
    public void Nested_sections_bind()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:ControlLoop:Retry:MaxRetries", "7"),
            ("Alberto:Modules:orders:ControlLoop:Leases:Enabled", "true"),
            ("Alberto:Modules:orders:ControlLoop:Leases:ReplicaId", "pod-3"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.ControlLoop.Retry.MaxRetries.Should().Be(7);
        result.ControlLoop.Retry.RetryDelay.Should().Be(TimeSpan.FromSeconds(1));
        result.ControlLoop.Leases.Enabled.Should().BeTrue();
        result.ControlLoop.Leases.ReplicaId.Should().Be("pod-3");
    }

    [Fact]
    public void Another_modules_section_is_ignored()
    {
        var configuration = Configuration(("Alberto:Modules:billing:ControlLoop:BatchSize", "1"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.ControlLoop.BatchSize.Should().Be(500);
    }

    [Fact]
    public void Telemetry_and_checkpoint_sections_bind()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:Telemetry:Enabled", "false"),
            ("Alberto:Modules:orders:Checkpoints:OrphanPolicy", "Strict"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.Telemetry.Enabled.Should().BeFalse();
        result.Checkpoints.OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Strict);
    }
}
