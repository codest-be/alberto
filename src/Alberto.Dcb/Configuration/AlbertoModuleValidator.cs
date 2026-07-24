using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// Checks a module declaration at startup, under <c>ValidateOnStart()</c>. Collects every
/// problem rather than throwing on the first, so one restart surfaces the whole list.
/// </summary>
public sealed class AlbertoModuleValidator : IValidateOptions<AlbertoModuleDefinition>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AlbertoModuleDefinition options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = Collect(options);
        if (failures.Count == 0)
            return ValidateOptionsResult.Success;

        return ValidateOptionsResult.Fail(
            AlbertoValidationReport.Describe(options.ModuleKey, failures));
    }

    /// <summary>
    /// Returns every configuration problem in <paramref name="definition"/>. Exposed separately
    /// so tests and diagnostics can inspect codes instead of parsing a message.
    /// </summary>
    public IReadOnlyList<AlbertoValidationFailure> Collect(AlbertoModuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var failures = new List<AlbertoValidationFailure>();

        ValidateBackend(definition, failures);
        ValidateControlLoop(definition, failures);
        ValidateProcessors(definition, failures);

        if (definition.Backend is not null)
            failures.AddRange(definition.Backend.Validate(definition));

        return failures;
    }

    private static void ValidateBackend(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
    {
        if (definition.Backend is null)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0001",
                "No event store backend is configured.",
                $"Add .WithPostgres(...) or .WithInMemory() inside AddAlberto(\"{definition.ModuleKey}\", ...)."));
            return;
        }

        if (definition.TenancyEnabled && !definition.Backend.SupportsTenancy)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0003",
                $"The module declares .WithTenancy() but the {definition.Backend.Name} backend does not support tenancy.",
                "Remove .WithTenancy() or switch to a backend that supports it, such as .WithPostgres(...)."));
        }
    }

    private static void ValidateControlLoop(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
    {
        var loop = definition.ControlLoop;
        var path = definition.ConfigurationPath;

        if (loop.PollingInterval <= TimeSpan.Zero)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0004",
                $"ControlLoop.PollingInterval is {loop.PollingInterval}, which is not a positive duration.",
                $"Set a positive interval via .WithControlLoop(o => o with {{ PollingInterval = ... }}) or '{path}:ControlLoop:PollingInterval'."));
        }

        if (loop.HeadRefreshInterval <= TimeSpan.Zero)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0004",
                $"ControlLoop.HeadRefreshInterval is {loop.HeadRefreshInterval}, which is not a positive duration.",
                $"Set a positive interval via .WithControlLoop(o => o with {{ HeadRefreshInterval = ... }}) or '{path}:ControlLoop:HeadRefreshInterval'."));
        }

        if (loop.BatchSize <= 0)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0004",
                $"ControlLoop.BatchSize is {loop.BatchSize}, which is not a positive count.",
                $"Set a positive batch size via .WithControlLoop(o => o with {{ BatchSize = ... }}) or '{path}:ControlLoop:BatchSize'."));
        }

        if (loop.HeadWindowSize <= 0)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0004",
                $"ControlLoop.HeadWindowSize is {loop.HeadWindowSize}, which is not a positive count.",
                $"Set a positive window size via .WithControlLoop(o => o with {{ HeadWindowSize = ... }}) or '{path}:ControlLoop:HeadWindowSize'."));
        }

        if (loop.Retry.MaxRetries < 0)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0007",
                $"ControlLoop.Retry.MaxRetries is {loop.Retry.MaxRetries}. Use 0 to disable retries.",
                $"Set a non-negative count via '{path}:ControlLoop:Retry:MaxRetries'."));
        }

        if (loop.Retry.BackoffMultiplier < 1.0)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0007",
                $"ControlLoop.Retry.BackoffMultiplier is {loop.Retry.BackoffMultiplier}, which would shrink the delay on each retry.",
                $"Use 1.0 for a constant delay, or a larger value to back off, via '{path}:ControlLoop:Retry:BackoffMultiplier'."));
        }
    }

    private static void ValidateProcessors(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
    {
        foreach (var duplicate in definition.Processors
                     .GroupBy(p => p.ProcessorId, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            var types = duplicate
                .Select(p => p.HandlerType?.Name)
                .Where(n => n is not null)
                .ToArray();

            var attribution = types.Length > 0
                ? $" Declared by {string.Join(" and ", types)}."
                : string.Empty;

            failures.Add(new AlbertoValidationFailure(
                "ALB0002",
                $"{duplicate.Count()} processors share the id '{duplicate.Key}'. Processor ids are checkpoint keys and must be unique within a module.{attribution}",
                "Give one of them a distinct id with [ProcessorId(\"...\")] on its handler type."));
        }

        foreach (var processor in definition.Processors)
        {
            if (string.IsNullOrWhiteSpace(processor.ProcessorId)
                || processor.ProcessorId.Any(char.IsWhiteSpace))
            {
                failures.Add(new AlbertoValidationFailure(
                    "ALB0006",
                    $"The processor id '{processor.ProcessorId}' is empty or contains whitespace.",
                    "Processor ids are used as checkpoint keys. Use a non-empty identifier without whitespace."));
            }

            if (processor.Execution is { MaxConcurrency: > 1, BatchingMode: ProcessorBatchingMode.Disabled })
            {
                failures.Add(new AlbertoValidationFailure(
                    "ALB0005",
                    $"Processor '{processor.ProcessorId}' asks for MaxConcurrency {processor.Execution.MaxConcurrency} while batching is Disabled. Concurrency only applies within a batch.",
                    "Set BatchingMode to Required or IfSupported, or set MaxConcurrency back to 1."));
            }
        }
    }
}
