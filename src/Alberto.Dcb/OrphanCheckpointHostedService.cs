using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb;

/// <summary>
/// Compares the checkpoints in the store against the processors this module declares and reports
/// the ones nothing claims.
/// </summary>
/// <remarks>
/// An orphaned checkpoint almost always means a handler was renamed: the new name has no stored
/// position, so it replays from the beginning, while the old name's position sits unused. That is
/// silent, expensive, and easy to miss, so it is a warning in Development and a startup failure
/// everywhere else.
/// </remarks>
internal sealed class OrphanCheckpointHostedService(
    AlbertoModuleDefinition definition,
    ICheckpointInventory? inventory,
    ILogger<OrphanCheckpointHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (definition.Checkpoints.OrphanPolicy == OrphanCheckpointPolicy.Off)
            return;

        if (inventory is null)
        {
            logger.LogDebug(
                "Skipping the orphaned-checkpoint check for module {ModuleKey}: the checkpoint " +
                "store does not implement ICheckpointInventory.",
                definition.ModuleKey);
            return;
        }

        var declared = definition.Processors
            .Select(p => p.ProcessorId)
            .ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<string> stored;
        try
        {
            stored = await inventory.ListProcessorIdsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database may be temporarily unreachable (e.g., a degraded shard at startup).
            // The orphan check is a safety feature, not a critical startup requirement; log a
            // warning and skip rather than propagating an exception that would stop the host.
            logger.LogWarning(ex,
                "Skipping orphaned-checkpoint check for module {ModuleKey}: could not read the " +
                "checkpoint store ({ExceptionType}).",
                definition.ModuleKey, ex.GetType().Name);
            return;
        }

        // Shadow rebuild keys ("processorId::rebuild::N") are artefacts of the rebuild
        // protocol, not independently declared processors. The coordinator's sweep removes them
        // once a version's grace period elapses, but a crash between the flip and the sweep,
        // an older host that did not perform the reset, or a remote replica's shadow loop can
        // leave one behind. Treating them as orphans and failing startup under Strict policy
        // would brick the host until someone deleted the row by hand; ignoring them here is
        // the safe fallback while the coordinator's ClearAsync is the authoritative cleaner.
        var orphans = stored
            .Where(id => !declared.Contains(id) && !RebuildableProjection.IsShadowProcessorId(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (orphans.Count == 0)
            return;

        var renameCommands = string.Join(Environment.NewLine,
            orphans.Select(id =>
                $"  alberto ops checkpoint rename --module {definition.ModuleKey} --from {id} --to <new-processor-id>"));
        var message =
            $"Module '{definition.ModuleKey}' has {orphans.Count} checkpoint(s) that no declared " +
            $"processor claims: [{string.Join(", ", orphans)}]. This usually means a handler was " +
            "renamed, in which case the new processor will replay from the beginning. " +
            $"Carry each position over with:{Environment.NewLine}{renameCommands}{Environment.NewLine}" +
            "Pin the old id instead with [ProcessorId(\"...\")], or set " +
            $"'{definition.ConfigurationPath}:Checkpoints:OrphanPolicy' to Warn or Off.";

        if (definition.Checkpoints.OrphanPolicy == OrphanCheckpointPolicy.Strict)
            throw new InvalidOperationException(message);

        logger.LogWarning("{OrphanCheckpointWarning}", message);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
