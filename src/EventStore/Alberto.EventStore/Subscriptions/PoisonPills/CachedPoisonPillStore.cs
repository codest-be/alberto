using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.PoisonPills;

/// <summary>
/// Poison pill store that caches read results to reduce database queries.
/// Most poison pill checks return null (no poison pill), so caching significantly
/// reduces database load during high-throughput scenarios.
/// </summary>
public sealed class CachedPoisonPillStore(
    IPoisonPillStore innerStore,
    int maxCacheSize,
    ILogger<CachedPoisonPillStore> logger)
    : IPoisonPillStore
{
    private readonly ConcurrentDictionary<(string SubscriptionId, long Position), PoisonPill?> _cache = new();

    public async ValueTask<PoisonPill?> GetPoisonPill(
        string subscriptionId,
        long position,
        CancellationToken ct)
    {
        var key = (subscriptionId, position);

        // Check cache first
        if (_cache.TryGetValue(key, out var cachedResult))
        {
            return cachedResult;
        }

        // Cache miss - query inner store
        var poisonPill = await innerStore.GetPoisonPill(subscriptionId, position, ct);

        // Cache the result (even if null - negative caching is important!)
        TryAddToCache(key, poisonPill);

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

    private void TryAddToCache((string SubscriptionId, long Position) key, PoisonPill? poisonPill)
    {
        // Simple cache eviction: if cache is full, don't add new entries
        // This is a basic strategy - could be improved with LRU or other eviction policies
        if (_cache.Count >= maxCacheSize)
        {
            logger.LogWarning(
                "Poison pill cache is full ({Count} entries), skipping cache for subscription '{SubscriptionId}' at position {Position}",
                _cache.Count,
                key.SubscriptionId,
                key.Position
            );
            return;
        }

        _cache.TryAdd(key, poisonPill);
    }
}