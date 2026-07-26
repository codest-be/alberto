using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

/// <summary>
/// Tests for per-processor execution option overlays from configuration.
/// Finding I1: ApplyConfiguration must honour Processors:{processorId} sections.
/// </summary>
public class ProcessorExecutionConfigurationTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ProcessorDeclaration Processor(string id, ProcessorBatchingMode batching = ProcessorBatchingMode.Required, int maxConcurrency = 1) =>
        new()
        {
            ProcessorId = id,
            Kind = ProcessorKind.Reactor,
            Execution = new ProcessorExecutionOptions { BatchingMode = batching, MaxConcurrency = maxConcurrency },
        };

    private static AlbertoModuleDefinition Definition(params ProcessorDeclaration[] processors) => new()
    {
        ModuleKey = "orders",
        Processors = [.. processors],
    };

    // ── I1.1: config overlay reaches the named processor ────────────────────

    [Fact]
    public void Config_MaxConcurrency_overrides_the_target_processor()
    {
        var definition = Definition(
            Processor("send-email", batching: ProcessorBatchingMode.Required, maxConcurrency: 1),
            Processor("send-sms",   batching: ProcessorBatchingMode.Required, maxConcurrency: 1));

        var configuration = Configuration(
            ("Alberto:Modules:orders:Processors:send-email:MaxConcurrency", "4"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(definition, configuration);

        result.Processors.Single(p => p.ProcessorId == "send-email")
            .Execution.MaxConcurrency.Should().Be(4,
                "config supplied MaxConcurrency = 4 for send-email");

        result.Processors.Single(p => p.ProcessorId == "send-sms")
            .Execution.MaxConcurrency.Should().Be(1,
                "send-sms was not mentioned in config and must be unchanged");
    }

    [Fact]
    public void Config_BatchingMode_overrides_the_target_processor()
    {
        var definition = Definition(
            Processor("send-email", batching: ProcessorBatchingMode.Required, maxConcurrency: 1));

        var configuration = Configuration(
            ("Alberto:Modules:orders:Processors:send-email:BatchingMode", "Disabled"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(definition, configuration);

        result.Processors.Single(p => p.ProcessorId == "send-email")
            .Execution.BatchingMode.Should().Be(ProcessorBatchingMode.Disabled);
    }

    // ── I1.2: absent or unmatched sections are harmless ─────────────────────

    [Fact]
    public void Config_section_for_unknown_processor_does_not_throw()
    {
        var definition = Definition(Processor("send-email"));

        var configuration = Configuration(
            ("Alberto:Modules:orders:Processors:does-not-exist:MaxConcurrency", "4"));

        // Must not throw; the unknown processor id is simply not matched.
        var act = () => AlbertoModuleDefinition.ApplyConfiguration(definition, configuration);

        act.Should().NotThrow();

        var result = AlbertoModuleDefinition.ApplyConfiguration(definition, configuration);
        result.Processors.Single(p => p.ProcessorId == "send-email")
            .Execution.MaxConcurrency.Should().Be(1, "send-email was not in config");
    }

    [Fact]
    public void Absent_processor_config_leaves_defaults_intact()
    {
        var definition = Definition(Processor("send-email"));
        var configuration = Configuration(); // no Processors section at all

        var result = AlbertoModuleDefinition.ApplyConfiguration(definition, configuration);

        result.Processors.Single(p => p.ProcessorId == "send-email")
            .Execution.Should().Be(ProcessorExecutionOptions.Default);
    }

    // ── I1.3: config-only ALB0005 violation is caught ───────────────────────

    [Fact]
    public void Config_only_ALB0005_violation_is_detected()
    {
        // MaxConcurrency > 1 with BatchingMode = Disabled is only introduced via config.
        // The code sets up a valid combination; config makes it illegal.
        var definition = Definition(
            Processor("send-email", batching: ProcessorBatchingMode.Required, maxConcurrency: 1));

        var configuration = Configuration(
            ("Alberto:Modules:orders:Processors:send-email:MaxConcurrency", "4"),
            ("Alberto:Modules:orders:Processors:send-email:BatchingMode", "Disabled"));

        var after = AlbertoModuleDefinition.ApplyConfiguration(definition, configuration);

        new AlbertoModuleValidator()
            .Collect(after)
            .Should().ContainSingle(f => f.Code == "ALB0005",
                "MaxConcurrency=4 + BatchingMode=Disabled introduced via config is still invalid");
    }
}
