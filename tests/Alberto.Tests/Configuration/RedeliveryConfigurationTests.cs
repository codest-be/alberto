using Alberto.Configuration;
using Alberto.Subscriptions;
using Alberto.Tests.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Redelivery and halt settings: they bind from configuration at module and processor level,
/// invalid values are rejected at startup, and a processor that halts takes no redelivery.
/// </summary>
public class RedeliveryConfigurationTests
{
    private const string ModuleKey = "orders";
    private const string Prefix = $"Alberto:Modules:{ModuleKey}";

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static ProcessorDeclaration Processor(string id, ProcessorKind kind = ProcessorKind.Reactor, bool halts = false) => new()
    {
        ProcessorId = id,
        Kind = kind,
        HaltsOnFailure = halts,
        Execution = ProcessorExecutionOptions.Default,
    };

    private static AlbertoModuleDefinition Apply(IConfiguration configuration, params ProcessorDeclaration[] processors) =>
        AlbertoModuleDefinition.ApplyConfiguration(
            new AlbertoModuleDefinition { ModuleKey = ModuleKey, Backend = new FakeBackend(), Processors = [.. processors] },
            configuration);

    private static IReadOnlyList<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) =>
        new AlbertoModuleValidator().Collect(definition);

    [Fact]
    public void Module_and_processor_values_bind_and_the_processor_wins()
    {
        var definition = Apply(
            Config(
                ($"{Prefix}:ControlLoop:Redelivery:MaxRedeliveries", "3"),
                ($"{Prefix}:ControlLoop:Redelivery:FirstDelay", "00:05:00"),
                ($"{Prefix}:Processors:mail:Redelivery:MaxRedeliveries", "7")),
            Processor("mail"), Processor("sms"));

        var policies = new ProcessorPolicies(definition.ControlLoop, definition.Processors);

        definition.ControlLoop.Redelivery.MaxRedeliveries.Should().Be(3);
        policies.For("mail").Redelivery.MaxRedeliveries.Should().Be(7);
        policies.For("mail").Redelivery.FirstDelay.Should().Be(TimeSpan.FromMinutes(5), "unset fields follow the module value");
        policies.For("sms").Redelivery.MaxRedeliveries.Should().Be(3);
        Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Redelivery_and_halt_keys_are_known_and_typos_are_flagged()
    {
        Validate(Apply(Config(
                ($"{Prefix}:ControlLoop:Redelivery:RedeliverWhen", "Transient"),
                ($"{Prefix}:ControlLoop:Halt:MaxDelay", "00:02:00"),
                ($"{Prefix}:Processors:mail:Redelivery:Enabled", "false")),
                Processor("mail")))
            .Should().NotContain(f => f.Code == "ALB0008");

        Validate(Apply(Config(($"{Prefix}:ControlLoop:Redelivery:MaxRedeliverys", "3")), Processor("mail")))
            .Should().Contain(f => f.Code == "ALB0008");
    }

    [Theory]
    [InlineData("ControlLoop:Redelivery:MaxRedeliveries", "-1")]
    [InlineData("ControlLoop:Redelivery:BackoffMultiplier", "0.5")]
    [InlineData("ControlLoop:Redelivery:FirstDelay", "00:00:00")]
    [InlineData("ControlLoop:Redelivery:MaxDelay", "-00:01:00")]
    [InlineData("ControlLoop:Redelivery:FirstDelay", "1.00:00:00")] // longer than the 12 h default MaxDelay
    [InlineData("ControlLoop:Halt:BackoffMultiplier", "0")]
    [InlineData("ControlLoop:Halt:InitialDelay", "00:00:00")]
    [InlineData("ControlLoop:Halt:InitialDelay", "01:00:00")] // longer than the 1 min default MaxDelay
    [InlineData("Processors:mail:Redelivery:BackoffMultiplier", "0.9")]
    [InlineData("Processors:mail:Redelivery:MaxRedeliveries", "-5")]
    public void Invalid_values_are_rejected(string key, string value)
    {
        Validate(Apply(Config(($"{Prefix}:{key}", value)), Processor("mail")))
            .Should().Contain(f => f.Code == "ALB0007");
    }

    [Fact]
    public void Redelivery_on_a_projection_is_rejected()
    {
        var failures = Validate(Apply(
            Config(($"{Prefix}:Processors:read-model:Redelivery:Enabled", "true")),
            Processor("read-model", ProcessorKind.Projection)));

        failures.Should().ContainSingle(f => f.Code == "ALB0028").Which.Problem.Should().Contain("read-model");
    }

    [Fact]
    public void DeadLetterOnMaxRetries_on_a_projection_is_rejected()
    {
        Validate(Apply(
                Config(($"{Prefix}:Processors:read-model:Retry:DeadLetterOnMaxRetries", "true")),
                Processor("read-model", ProcessorKind.Projection)))
            .Should().ContainSingle(f => f.Code == "ALB0028");
    }

    [Fact]
    public void Redelivery_on_a_halting_reactor_is_rejected_but_fine_on_a_plain_one()
    {
        var config = Config(($"{Prefix}:Processors:p:Redelivery:MaxRedeliveries", "2"));

        Validate(Apply(config, Processor("p", halts: true))).Should().ContainSingle(f => f.Code == "ALB0028");
        Validate(Apply(config, Processor("p"))).Should().BeEmpty();
    }

    [Fact]
    public void Module_level_redelivery_does_not_trip_the_projection_rule()
    {
        Validate(Apply(
                Config(($"{Prefix}:ControlLoop:Redelivery:MaxRedeliveries", "2")),
                Processor("read-model", ProcessorKind.Projection)))
            .Should().BeEmpty();
    }

    [Fact]
    public void Halt_override_on_a_projection_is_allowed()
    {
        Validate(Apply(
                Config(($"{Prefix}:Processors:read-model:Halt:MaxDelay", "00:05:00")),
                Processor("read-model", ProcessorKind.Projection)))
            .Should().BeEmpty();
    }
}
