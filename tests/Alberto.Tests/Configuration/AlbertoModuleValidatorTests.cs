using System.Collections.Immutable;
using Alberto.Configuration;
using Alberto.Subscriptions;
using Alberto.Tests.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alberto.Tests.Configuration;

public class AlbertoModuleValidatorTests
{
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
        Assert.Empty(Run(Valid(Processor("orders-summary"))));
    }

    [Fact]
    public void A_module_without_a_backend_fails_with_ALB0001()
    {
        var failures = Run(Valid() with { Backend = null });

        Assert.Equal("ALB0001", Assert.Single(failures).Code);
    }

    [Fact]
    public void Duplicate_processor_ids_fail_with_ALB0002()
    {
        var failures = Run(Valid(Processor("same"), Processor("same")));

        Assert.Equal("ALB0002", Assert.Single(failures).Code);
        Assert.Contains("same", failures[0].Problem);
    }

    [Fact]
    public void Tenancy_on_a_backend_that_does_not_support_it_fails_with_ALB0003()
    {
        var definition = Valid() with { TenancyEnabled = true, Backend = new FakeBackend(supportsTenancy: false) };

        Assert.Equal("ALB0003", Assert.Single(Run(definition)).Code);
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

        Assert.Contains(Run(definition), f => f.Code == "ALB0004");
    }

    [Fact]
    public void Concurrency_without_batching_fails_with_ALB0005()
    {
        var execution = new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled, MaxConcurrency = 4 };

        var failures = Run(Valid(Processor("busy", execution)));

        Assert.Equal("ALB0005", Assert.Single(failures).Code);
        Assert.Contains("busy", failures[0].Problem);
    }

    [Fact]
    public void BatchingMode_Required_with_MaxConcurrency_greater_than_one_fails_with_ALB0022()
    {
        var execution = new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Required, MaxConcurrency = 4 };

        var failures = Run(Valid(Processor("batch-required", execution)));

        Assert.Equal("ALB0022", Assert.Single(failures).Code);
        Assert.Contains("batch-required", failures[0].Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("has space")]
    public void A_malformed_processor_id_fails_with_ALB0006(string processorId)
    {
        Assert.Contains(Run(Valid(Processor(processorId))), f => f.Code == "ALB0006");
    }

    [Fact]
    public void A_negative_retry_count_fails_with_ALB0007()
    {
        var definition = Valid() with
        {
            ControlLoop = new ControlLoopOptions { Retry = new RetryOptions { MaxRetries = -1 } },
        };

        Assert.Contains(Run(definition), f => f.Code == "ALB0007");
    }

    [Fact]
    public void Backend_failures_are_included()
    {
        var backendFailure = new AlbertoValidationFailure("ALB9999", "Backend problem.", "Backend remedy.");
        var definition = Valid() with { Backend = new FakeBackend(true, backendFailure) };

        Assert.Contains(backendFailure, Run(definition));
    }

    [Fact]
    public void Every_failure_is_reported_at_once_rather_than_the_first()
    {
        var definition = Valid(Processor("dup"), Processor("dup")) with
        {
            Backend = null,
            ControlLoop = new ControlLoopOptions { BatchSize = 0 },
        };

        var codes = Run(definition).Select(f => f.Code).ToArray();
        Assert.All(new[] { "ALB0001", "ALB0002", "ALB0004" }, code => Assert.Contains(code, codes));
    }

    [Fact]
    public void Validate_fails_with_a_message_naming_every_problem()
    {
        var definition = Valid(Processor("dup"), Processor("dup")) with { Backend = null };

        var result = new AlbertoModuleValidator().Validate("orders", definition);

        Assert.True(result.Failed);
        Assert.Contains("ALB0001", result.FailureMessage);
        Assert.Contains("ALB0002", result.FailureMessage);
    }

    [Fact]
    public void Validate_succeeds_for_a_well_formed_module()
    {
        Assert.True(new AlbertoModuleValidator()
            .Validate("orders", Valid(Processor("ok")))
            .Succeeded);
    }
}
