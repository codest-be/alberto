using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.PoisonPills;

/// <summary>
/// Poison pill store that maintains an in-memory cache as the source of truth.
/// On first access, loads all poison pills from the underlying store once (lazy initialization).
/// During operation, serves all reads from memory (zero database queries) and writes through to the database.
/// Perfect for single-active subscription processor model where one instance handles all subscriptions.
/// </summary>
public sealed class CachedPoisonPillStore(
    IPoisonPillStore innerStore,
    ILogger<CachedPoisonPillStore> logger)
    : IPoisonPillStore
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly ConcurrentDictionary<(string SubscriptionId, long Position), PoisonPill> _poisonPills = new();
    private bool _initialized;

    public async ValueTask<PoisonPill?> GetPoisonPill(
        string subscriptionId,
        long position,
        CancellationToken ct)
    {
        await EnsureInitialized(ct);

        // Pure in-memory lookup - never hits database after initialization
        var key = (subscriptionId, position);
        _poisonPills.TryGetValue(key, out var poisonPill);

        return poisonPill;
    }

    public async ValueTask StorePoisonPill(PoisonPill poisonPill, CancellationToken ct)
    {
        await EnsureInitialized(ct);

        // Write to memory first
        var key = (poisonPill.SubscriptionId, poisonPill.GlobalPosition);
        _poisonPills.AddOrUpdate(key, poisonPill, (_, _) => poisonPill);

        // Write through to database
        await innerStore.StorePoisonPill(poisonPill, ct);

        logger.LogDebug(
            "Stored poison pill for subscription '{SubscriptionId}' at position {Position} (in-memory + database)",
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
        await EnsureInitialized(ct);

        // Find and update in memory
        foreach (var kvp in _poisonPills)
        {
            if (kvp.Value.Id == poisonPillId)
            {
                var resolvedPoisonPill = kvp.Value with { ResolvedAt = DateTimeOffset.UtcNow, ResolvedBy = resolvedBy, ResolutionAction = action };

                _poisonPills.TryUpdate(kvp.Key, resolvedPoisonPill, kvp.Value);

                logger.LogDebug(
                    "Updated cached poison pill {PoisonPillId} to resolved state (in-memory)",
                    poisonPillId
                );
                break;
            }
        }

        // Write through to database
        await innerStore.ResolvePoisonPill(poisonPillId, resolvedBy, action, ct);
    }

    public async ValueTask<IReadOnlyList<PoisonPill>> GetAllPoisonPills(CancellationToken ct)
    {
        await EnsureInitialized(ct);

        // Return from in-memory cache
        return _poisonPills.Values.ToList();
    }

    /// <summary>
    /// Ensures the cache is initialized by loading all poison pills from the underlying store.
    /// Uses lazy initialization - called automatically on first access.
    /// Thread-safe using semaphore for async/await compatibility.
    /// </summary>
    private async ValueTask EnsureInitialized(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initializationLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_initialized)
                return;

            var allPills = await innerStore.GetAllPoisonPills(ct);

            foreach (var pill in allPills)
            {
                _poisonPills[(pill.SubscriptionId, pill.GlobalPosition)] = pill;
            }

            _initialized = true;

            logger.LogInformation(
                "Initialized poison pill cache with {Count} poison pills",
                allPills.Count
            );
        }
        finally
        {
            _initializationLock.Release();
        }
    }
}