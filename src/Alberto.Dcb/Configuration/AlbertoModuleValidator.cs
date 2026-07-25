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
            AlbertoValidationReport.Describe(options.DisplayName, failures));
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
        ValidateUnknownKeys(definition, failures);
        ValidateTenancy(definition, failures);

        if (definition.Backend is not null)
            failures.AddRange(definition.Backend.Validate(definition));

        // Each shard's backend is validated in its own right, so a bad pool size or an unsafe
        // schema is reported against the shard that has it rather than against the module.
        foreach (var shard in definition.Tenancy.Shards)
        {
            var shardDefinition = ShardExpansion.ForShard(definition, shard);
            foreach (var failure in shard.Backend.Validate(shardDefinition))
            {
                failures.Add(failure with
                {
                    Problem = $"Shard '{shard.ShardId}': {failure.Problem}",
                });
            }
        }

        return failures;
    }

    /// <remarks>
    /// Sharding is checked only on the module's own definition. The per-shard copies carry an
    /// empty tenancy declaration, so none of this runs once per shard.
    /// </remarks>
    private static void ValidateTenancy(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
    {
        var tenancy = definition.Tenancy;
        var path = $"{definition.ConfigurationPath}:Tenancy";

        foreach (var shardId in tenancy.UndeclaredConfiguredShards)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0015",
                $"Configuration declares shard '{shardId}', which the module does not.",
                $"Shard services are registered while the container is built, before configuration " +
                $"is read, so a shard that exists only in configuration can never serve a request. " +
                $"Add .AddShard(\"{shardId}\", ...) in code, or remove '{path}:Shards:{shardId}'."));
        }

        if (!tenancy.IsSharded)
            return;

        if (!definition.TenancyEnabled)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0010",
                "The module declares shards but not tenancy. A shard routes tenants, so there is nothing to route.",
                "Declare the shards inside .WithTenancy(t => ...) rather than alongside it."));
        }

        if (definition.Backend is { SupportsTenancy: false } backend)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0011",
                $"The module declares shards but the {backend.Name} backend does not support tenancy.",
                "Switch to a backend that supports it, such as .WithPostgres(...)."));
        }

        foreach (var shard in tenancy.Shards)
        {
            if (!Tenancy.ShardKey.IsValidShardId(shard.ShardId))
            {
                failures.Add(new AlbertoValidationFailure(
                    "ALB0012",
                    $"Shard id '{shard.ShardId}' is not a safe identifier.",
                    "A shard id becomes part of a DI key, a metric tag and a lease holder name. " +
                    "Use a lowercase identifier that starts with a letter and contains only " +
                    "lowercase letters, digits and underscores (maximum 63 characters)."));
            }
        }

        foreach (var duplicate in tenancy.Shards
                     .GroupBy(s => s.ShardId, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0012",
                $"{duplicate.Count()} shards share the id '{duplicate.Key}'. A shard id is written into the " +
                "catalog next to every tenant assigned to it, so it must identify exactly one database.",
                "Give each .AddShard(...) call a distinct id."));
        }

        if (tenancy.DefaultShardId is { } defaultShard
            && !tenancy.ShardIds.Contains(defaultShard, StringComparer.Ordinal))
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0013",
                $"The default shard '{defaultShard}' is not one of the declared shards " +
                $"[{string.Join(", ", tenancy.ShardIds)}].",
                $"Name a declared shard in .WithDefaultShard(...) or '{path}:DefaultShard'."));
        }

        // Two shards pointing at the same database would each run their own control loops over
        // the same events and each write their own checkpoints, so every event would be processed
        // twice. It is also exactly what a copy-pasted connection string looks like.
        foreach (var collision in tenancy.Shards
                     .Where(s => s.Backend.StorageIdentity is not null)
                     .GroupBy(s => s.Backend.StorageIdentity, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0016",
                $"Shards [{string.Join(", ", collision.Select(s => s.ShardId))}] all resolve to {collision.Key}. " +
                "Separate shards must be separate storage.",
                $"Give each shard its own database, or its own schema, under '{path}:Shards'."));
        }

        if (tenancy.Catalog is null)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0014",
                "The module declares shards but no catalog, so there is nowhere to record which shard a tenant is in.",
                "Declare one with .WithCatalog(o => o with { ConnectionString = ... }). Point it at a " +
                "control database rather than at one of the shards, so no shard is load-bearing for " +
                "routing to the others."));
        }
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

        if (loop.Rebuilds.Enabled)
        {
            if (loop.Rebuilds.PollingInterval <= TimeSpan.Zero)
            {
                failures.Add(new AlbertoValidationFailure(
                    "ALB0009",
                    $"ControlLoop.Rebuilds.PollingInterval is {loop.Rebuilds.PollingInterval}, which is not a positive duration.",
                    $"Set a positive interval via .WithRebuilds(pollingInterval: ...) or '{path}:ControlLoop:Rebuilds:PollingInterval'."));
            }

            if (loop.Rebuilds.VersionRefreshInterval <= TimeSpan.Zero)
            {
                failures.Add(new AlbertoValidationFailure(
                    "ALB0009",
                    $"ControlLoop.Rebuilds.VersionRefreshInterval is {loop.Rebuilds.VersionRefreshInterval}, which is not a positive duration.",
                    $"Set a positive interval via .WithRebuilds(pollingInterval: ...) or '{path}:ControlLoop:Rebuilds:VersionRefreshInterval'."));
            }
        }
    }

    private static void ValidateUnknownKeys(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
    {
        foreach (var key in definition.UnknownConfigurationKeys)
        {
            var remedy = key.Suggestion is not null
                ? $"Did you mean '{key.Suggestion}'? Correct or remove this key."
                : "This key is not recognised. Correct or remove it.";

            failures.Add(new AlbertoValidationFailure(
                "ALB0008",
                $"Unknown configuration key '{key.FullKey}'.",
                remedy));
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
