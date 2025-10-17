using System.Collections.Concurrent;
using Alberto.EventStore.Subscriptions.PoisonPills;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.InMemory.Subscriptions.PoisonPills;

/// <summary>
/// In-memory implementation of poison pill store for testing and development
/// </summary>
public sealed class InMemoryPoisonPillStore(ILogger<InMemoryPoisonPillStore> logger) : IPoisonPillStore
{
    private readonly ConcurrentDictionary<(string SubscriptionId, long Position), PoisonPill> _poisonPills = new();
    private readonly ConcurrentDictionary<Guid, PoisonPill> _poisonPillsById = new();

    public ValueTask StorePoisonPill(PoisonPill poisonPill, CancellationToken ct)
    {
        var key = (poisonPill.SubscriptionId, poisonPill.GlobalPosition);

        _poisonPills.AddOrUpdate(key, poisonPill,
            (_, existing) => existing with
            {
                RetryCount = poisonPill.RetryCount,
                LastFailedAt = poisonPill.LastFailedAt,
                ErrorMessage = poisonPill.ErrorMessage,
                StackTrace = poisonPill.StackTrace
            });

        _poisonPillsById.AddOrUpdate(poisonPill.Id, poisonPill, (_, _) => poisonPill);

        logger.LogError(
            "Stored poison pill for subscription '{SubscriptionId}' at position {Position} after {RetryCount} retries",
            poisonPill.SubscriptionId,
            poisonPill.GlobalPosition,
            poisonPill.RetryCount
        );

        return ValueTask.CompletedTask;
    }

    public ValueTask<PoisonPill?> GetPoisonPill(
        string subscriptionId,
        long position,
        CancellationToken ct)
    {
        var key = (subscriptionId, position);
        _poisonPills.TryGetValue(key, out var poisonPill);
        return ValueTask.FromResult(poisonPill);
    }

    public ValueTask ResolvePoisonPill(
        Guid poisonPillId,
        string resolvedBy,
        string action,
        CancellationToken ct)
    {
        if (_poisonPillsById.TryGetValue(poisonPillId, out var poisonPill))
        {
            var resolvedPoisonPill = poisonPill with { ResolvedAt = DateTimeOffset.UtcNow, ResolvedBy = resolvedBy, ResolutionAction = action };

            var key = (poisonPill.SubscriptionId, poisonPill.GlobalPosition);
            _poisonPills.TryUpdate(key, resolvedPoisonPill, poisonPill);
            _poisonPillsById.TryUpdate(poisonPillId, resolvedPoisonPill, poisonPill);

            logger.LogInformation(
                "Resolved poison pill {PoisonPillId} with action '{Action}' by '{ResolvedBy}'",
                poisonPillId,
                action,
                resolvedBy
            );
        }
        else
        {
            logger.LogWarning(
                "Attempted to resolve non-existent poison pill {PoisonPillId}",
                poisonPillId
            );
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<PoisonPill>> GetAllPoisonPills(CancellationToken ct)
    {
        var allPills = _poisonPills.Values.ToList();
        logger.LogDebug("Retrieved {Count} poison pills", allPills.Count);
        return ValueTask.FromResult<IReadOnlyList<PoisonPill>>(allPills);
    }
}