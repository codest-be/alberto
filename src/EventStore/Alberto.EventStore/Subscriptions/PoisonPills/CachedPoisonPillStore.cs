using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.PoisonPills;

/// <summary>
/// Poison pill store that caches read results to reduce database queries.
/// Most poison pill checks return null (no poison pill), so caching significantly
/// reduces database load during high-throughput scenarios.
/// Uses position-based eviction to automatically remove obsolete entries as subscriptions advance.
/// </summary>
public sealed class CachedPoisonPillStore(
    IPoisonPillStore innerStore,
    ILogger<CachedPoisonPillStore> logger)
    : IPoisonPillStore
{
    private const int PositionWindow = 100;

    private readonly ConcurrentDictionary<(string SubscriptionId, long Position), PoisonPill?> _cache = new();
    private readonly ConcurrentDictionary<string, long> _highestPositions = new();

    public async ValueTask<PoisonPill?> GetPoisonPill(
        string subscriptionId,
        long position,
        CancellationToken ct)
    {
        var key = (subscriptionId, position);

        // Check cache first
        if (_cache.TryGetValue(key, out var cachedResult))
        {
            // Update highest position and clean up if needed
            UpdateHighestPositionAndCleanup(subscriptionId, position);
            return cachedResult;
        }

        // Cache miss - query inner store
        var poisonPill = await innerStore.GetPoisonPill(subscriptionId, position, ct);

        // Cache the result (even if null - negative caching is important!)
        _cache.TryAdd(key, poisonPill);

        // Update highest position and clean up old entries
        UpdateHighestPositionAndCleanup(subscriptionId, position);

        return poisonPill;
    }

    public async ValueTask StorePoisonPill(PoisonPill poisonPill, CancellationToken ct)
    {
        // Store in underlying store first
        await innerStore.StorePoisonPill(poisonPill, ct);

        // Update cache immediately to reflect the new poison pill
        var key = (poisonPill.SubscriptionId, poisonPill.GlobalPosition);
        _cache.AddOrUpdate(key, poisonPill, (_, _) => poisonPill);

        logger.LogDebug(
            "Cached poison pill for subscription '{SubscriptionId}' at position {Position}",
            poisonPill.SubscriptionId,
            poisonPill.GlobalPosition
        );
    }

    public async ValueTask ResolvePoisonPill(
        Guid poisonPillId,
        string resolvedBy,
        string action,
        CancellationToken ct)
    {
        // Resolve in underlying store first
        await innerStore.ResolvePoisonPill(poisonPillId, resolvedBy, action, ct);

        // Update cached entries to mark as resolved
        // We need to find the cached entry by ID and update it
        foreach (var kvp in _cache)
        {
            if (kvp.Value?.Id == poisonPillId)
            {
                var resolvedPoisonPill = kvp.Value with { ResolvedAt = DateTimeOffset.UtcNow, ResolvedBy = resolvedBy, ResolutionAction = action };

                _cache.TryUpdate(kvp.Key, resolvedPoisonPill, kvp.Value);

                logger.LogDebug(
                    "Updated cached poison pill {PoisonPillId} to resolved state",
                    poisonPillId
                );
                break;
            }
        }
    }

    private void UpdateHighestPositionAndCleanup(string subscriptionId, long position)
    {
        // Update highest position for this subscription
        var previousHighest = _highestPositions.AddOrUpdate(
            subscriptionId,
            position,
            (_, oldValue) => Math.Max(oldValue, position)
        );

        // If position has advanced, clean up old entries
        if (position > previousHighest)
        {
            var evictionThreshold = position - PositionWindow;
            var keysToRemove = _cache.Keys
                .Where(k => k.SubscriptionId == subscriptionId && k.Position < evictionThreshold)
                .ToList();

            var removedCount = keysToRemove.Count(key => _cache.TryRemove(key, out _));

            if (removedCount > 0)
            {
                logger.LogDebug(
                    "Evicted {Count} obsolete poison pill cache entries for subscription '{SubscriptionId}' (positions < {Threshold})",
                    removedCount,
                    subscriptionId,
                    evictionThreshold
                );
            }
        }
    }
}