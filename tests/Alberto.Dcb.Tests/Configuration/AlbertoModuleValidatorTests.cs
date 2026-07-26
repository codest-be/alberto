using System.Collections.Immutable;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class AlbertoModuleValidatorTests
{
    private sealed class FakeBackend(bool supportsTenancy = true, params AlbertoValidationFailure[] failures)
        : IAlbertoBackendDescriptor
    {
        public string Name => "Fake";
        public bool SupportsTenancy => supportsTenancy;
        public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;
        public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => failures;
        public void Register(AlbertoModuleContext context) { }
    }

    private static AlbertoModuleDefinition Valid(params ProcessorDeclaration[] processors) => new()
    {
        ModuleKey = "orders",
        Backend = new FakeBackend(),
        Processors = [.. processors],
    };

    private static ProcessorDeclaration Processor(
        string id,
        ProcessorExecutionOptions? execution = null) => new()
    {
        ProcessorId = id,
        Kind = ProcessorKind.Reactor,
        Execution = execution ?? ProcessorExecutionOptions.Default,
    };

    private static IReadOnlyList<AlbertoValidationFailure> Run(AlbertoModuleDefinition definition) =>
        new AlbertoModuleValidator().Collect(definition);

    [Fact]
    public void A_well_formed_module_produces_no_failures()
    {
        Run(Valid(Processor("orders-summary"))).Should().BeEmpty();
    }

    [Fact]
    public void A_module_without_a_backend_fails_with_ALB0001()
    {
        var failures = Run(Valid() with { Backend = null });

        failures.Should().ContainSingle().Which.Code.Should().Be("ALB0001");
    }

    [Fact]
    public void Duplicate_processor_ids_fail_with_ALB0002()
    {
        var failures = Run(Valid(Processor("same"), Processor("same")));

        failures.Should().ContainSingle().Which.Code.Should().Be("ALB0002");
        failures[0].Problem.Should().Contain("same");
    }

    [Fact]
    public void Tenancy_on_a_backend_that_does_not_support_it_fails_with_ALB0003()
    {
        var definition = Valid() with { TenancyEnabled = true, Backend = new FakeBackend(supportsTenancy: false) };

        Run(definition).Should().ContainSingle().Which.Code.Should().Be("ALB0003");
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(250, 0)]
    [InlineData(250, -5)]
    public void Non_positive_control_loop_values_fail_with_ALB0004(int pollingMilliseconds, int batchSize)
    {
        var definition = Valid() with
        {
            ControlLoop = new ControlLoopOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(pollingMilliseconds),
                BatchSize = batchSize,
            },
        };

        Run(definition).Should().Contain(f => f.Code == "ALB0004");
    }

    [Fact]
    public void Concurrency_without_batching_fails_with_ALB0005()
    {
        var execution = new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled, MaxConcurrency = 4 };

        var failures = Run(Valid(Processor("busy", execution)));

        failures.Should().ContainSingle().Which.Code.Should().Be("ALB0005");
        failures[0].Problem.Should().Contain("busy");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("has space")]
    public void A_malformed_processor_id_fails_with_ALB0006(string processorId)
    {
        Run(Valid(Processor(processorId))).Should().Contain(f => f.Code == "ALB0006");
    }

    [Fact]
    public void A_negative_retry_count_fails_with_ALB0007()
    {
        var definition = Valid() with
        {
            ControlLoop = new ControlLoopOptions { Retry = new RetryOptions { MaxRetries = -1 } },
        };

        Run(definition).Should().Contain(f => f.Code == "ALB0007");
    }

    [Fact]
    public void Backend_failures_are_included()
    {
        var backendFailure = new AlbertoValidationFailure("ALB9999", "Backend problem.", "Backend remedy.");
        var definition = Valid() with { Backend = new FakeBackend(true, backendFailure) };

        Run(definition).Should().Contain(backendFailure);
    }

    [Fact]
    public void Every_failure_is_reported_at_once_rather_than_the_first()
    {
        var definition = Valid(Processor("dup"), Processor("dup")) with
        {
            Backend = null,
            ControlLoop = new ControlLoopOptions { BatchSize = 0 },
        };

        Run(definition).Select(f => f.Code).Should().Contain(["ALB0001", "ALB0002", "ALB0004"]);
    }

    [Fact]
    public void Validate_fails_with_a_message_naming_every_problem()
    {
        var definition = Valid(Processor("dup"), Processor("dup")) with { Backend = null };

        var result = new AlbertoModuleValidator().Validate("orders", definition);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ALB0001").And.Contain("ALB0002");
    }

    [Fact]
    public void Validate_succeeds_for_a_well_formed_module()
    {
        new AlbertoModuleValidator()
            .Validate("orders", Valid(Processor("ok")))
            .Succeeded.Should().BeTrue();
    }
}
