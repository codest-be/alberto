using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Tests.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Tests for Finding I4: unknown configuration keys under Alberto:Modules:{key}
/// are reported as ALB0008 with an optional did-you-mean suggestion.
/// </summary>
public class UnknownConfigurationKeyTests
{
    private const string ModuleKey = "orders";

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static AlbertoModuleDefinition BaseDefinition(params ProcessorDeclaration[] processors) => new()
    {
        ModuleKey = ModuleKey,
        Backend = new FakeBackend(),
        Processors = [.. processors],
    };

    private static ProcessorDeclaration Processor(string id) => new()
    {
        ProcessorId = id,
        Kind = ProcessorKind.Reactor,
    };

    private static IReadOnlyList<AlbertoValidationFailure> Validate(
        AlbertoModuleDefinition definition,
        IConfiguration configuration)
    {
        var applied = AlbertoModuleDefinition.ApplyConfiguration(definition, configuration);
        return new AlbertoModuleValidator().Collect(applied);
    }

    // ── I4.1: valid keys produce no ALB0008 ─────────────────────────────────

    [Fact]
    public void Valid_ControlLoop_key_produces_no_ALB0008()
    {
        var failures = Validate(BaseDefinition(),
            Config(($"Alberto:Modules:{ModuleKey}:ControlLoop:BatchSize", "42")));

        failures.Should().NotContain(f => f.Code == "ALB0008");
    }

    [Fact]
    public void Valid_nested_Retry_key_produces_no_ALB0008()
    {
        var failures = Validate(BaseDefinition(),
            Config(($"Alberto:Modules:{ModuleKey}:ControlLoop:Retry:MaxRetries", "3")));

        failures.Should().NotContain(f => f.Code == "ALB0008");
    }

    [Fact]
    public void Valid_Telemetry_key_produces_no_ALB0008()
    {
        var failures = Validate(BaseDefinition(),
            Config(($"Alberto:Modules:{ModuleKey}:Telemetry:Enabled", "false")));

        failures.Should().NotContain(f => f.Code == "ALB0008");
    }

    [Fact]
    public void Valid_processor_execution_key_produces_no_ALB0008()
    {
        var failures = Validate(
            BaseDefinition(Processor("send-email")),
            Config(($"Alberto:Modules:{ModuleKey}:Processors:send-email:MaxConcurrency", "4"),
                   ($"Alberto:Modules:{ModuleKey}:Processors:send-email:BatchingMode", "Required")));

        failures.Should().NotContain(f => f.Code == "ALB0008");
    }

    [Fact]
    public void Processor_id_itself_is_never_flagged_as_unknown()
    {
        // The processor-id segment is dynamic and user-defined; only its children are validated.
        var failures = Validate(
            BaseDefinition(Processor("my-custom-reactor")),
            Config(($"Alberto:Modules:{ModuleKey}:Processors:my-custom-reactor:MaxConcurrency", "2")));

        failures.Should().NotContain(f => f.Code == "ALB0008");
    }

    // ── I4.2: unknown top-level section is caught ────────────────────────────

    [Fact]
    public void Unknown_top_level_section_is_ALB0008()
    {
        var failures = Validate(BaseDefinition(),
            Config(($"Alberto:Modules:{ModuleKey}:ControlLop:BatchSize", "42"))); // typo in section name

        failures.Should().ContainSingle(f => f.Code == "ALB0008" && f.Problem.Contains("ControlLop"));
    }

    // ── I4.3: unknown leaf key in a known section is caught ──────────────────

    [Fact]
    public void Typo_in_ControlLoop_leaf_key_is_ALB0008()
    {
        var failures = Validate(BaseDefinition(),
            Config(($"Alberto:Modules:{ModuleKey}:ControlLoop:BatchSizes", "42"))); // 's' appended

        failures.Should().ContainSingle(f => f.Code == "ALB0008" && f.Problem.Contains("BatchSizes"));
    }

    [Fact]
    public void Typo_in_nested_Retry_leaf_key_is_ALB0008()
    {
        var failures = Validate(BaseDefinition(),
            Config(($"Alberto:Modules:{ModuleKey}:ControlLoop:Retry:MaxRetriess", "5"))); // double 's'

        failures.Should().ContainSingle(f => f.Code == "ALB0008" && f.Problem.Contains("MaxRetriess"));
    }

    // ── I4.4: did-you-mean suggestions ──────────────────────────────────────

    [Fact]
    public void Close_typo_gets_a_did_you_mean_suggestion_in_the_remedy()
    {
        var failures = Validate(BaseDefinition(),
            Config(($"Alberto:Modules:{ModuleKey}:ControlLoop:BatchSizes", "42")));

        failures.Should().ContainSingle(f => f.Code == "ALB0008")
            .Which.Remedy.Should().Contain("BatchSize", "edit-distance 1 is within threshold");
    }

    [Fact]
    public void Completely_unrecognised_key_gets_no_suggestion()
    {
        var failures = Validate(BaseDefinition(),
            Config(($"Alberto:Modules:{ModuleKey}:ControlLoop:FooBarBazQux", "42")));

        var failure = failures.Should().ContainSingle(f => f.Code == "ALB0008").Subject;
        failure.Remedy.Should().NotContain("Did you mean",
            "no candidate is within the edit-distance threshold");
    }

    // ── I4.5: unknown processor leaf key is caught ───────────────────────────

    [Fact]
    public void Typo_in_processor_leaf_key_is_ALB0008()
    {
        var failures = Validate(
            BaseDefinition(Processor("send-email")),
            Config(($"Alberto:Modules:{ModuleKey}:Processors:send-email:MaxConcurrencyy", "4")));

        failures.Should().ContainSingle(f => f.Code == "ALB0008" && f.Problem.Contains("MaxConcurrencyy"));
    }

    [Fact]
    public void Processor_with_no_declared_processor_matching_still_validates_leaf_keys()
    {
        // Even when the processor id is not declared, leaf-key names are still validated.
        // (The overlay silently skips undeclared processors; ALB0008 still catches bad leaf names.)
        var failures = Validate(
            BaseDefinition(), // no processors declared
            Config(($"Alberto:Modules:{ModuleKey}:Processors:mystery-proc:BatchingMod", "Required")));

        // Should flag the unknown leaf 'BatchingMod' (typo of 'BatchingMode')
        failures.Should().ContainSingle(f => f.Code == "ALB0008" && f.Problem.Contains("BatchingMod"));
    }
}
