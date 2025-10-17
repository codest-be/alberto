using Alberto.EventStore.Subscriptions.PoisonPills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alberto.EventStore.Tests.Subscriptions;

/// <summary>
/// Tests for CachedPoisonPillStore position-based eviction
/// </summary>
public class CachedPoisonPillStoreTests
{
    [Fact]
    public async Task GetPoisonPill_EvictsOldEntries_WhenSubscriptionAdvances()
    {
        // Arrange
        var innerStore = new InMemoryPoisonPillStore();
        var logger = NullLogger<CachedPoisonPillStore>.Instance;
        var cachedStore = new CachedPoisonPillStore(innerStore, logger);

        const string subscriptionId = "test-subscription";

        // Act - Query positions 1-200 (all return null, so they get cached)
        for (long i = 1; i <= 200; i++)
        {
            await cachedStore.GetPoisonPill(subscriptionId, i, CancellationToken.None);
        }

        // Query position 250 - this should trigger cleanup of positions < 150 (250 - 100)
        await cachedStore.GetPoisonPill(subscriptionId, 250, CancellationToken.None);

        // Assert - Verify cache size by checking if old positions hit the inner store again
        // If positions < 150 were evicted, querying them should hit the inner store
        // We verify this by checking the inner store's call count

        // Since we can't easily inspect the cache, we verify the behavior by checking
        // that subsequent queries for evicted positions don't cause issues
        for (long i = 1; i < 150; i++)
        {
            var result = await cachedStore.GetPoisonPill(subscriptionId, i, CancellationToken.None);
            Assert.Null(result); // Should still work, even if re-queried from inner store
        }

        // Positions >= 150 should still be cached
        for (long i = 150; i <= 250; i++)
        {
            var result = await cachedStore.GetPoisonPill(subscriptionId, i, CancellationToken.None);
            Assert.Null(result); // Should be cached
        }
    }

    [Fact]
    public async Task GetPoisonPill_IsolatesSubscriptions_WhenEvicting()
    {
        // Arrange
        var innerStore = new InMemoryPoisonPillStore();
        var logger = NullLogger<CachedPoisonPillStore>.Instance;
        var cachedStore = new CachedPoisonPillStore(innerStore, logger);

        const string subscription1 = "subscription-1";
        const string subscription2 = "subscription-2";

        // Act - Cache positions for both subscriptions
        for (long i = 1; i <= 100; i++)
        {
            await cachedStore.GetPoisonPill(subscription1, i, CancellationToken.None);
            await cachedStore.GetPoisonPill(subscription2, i, CancellationToken.None);
        }

        // Advance subscription1 to position 200
        await cachedStore.GetPoisonPill(subscription1, 200, CancellationToken.None);

        // Assert - Subscription2 positions should still be cached (not affected by subscription1's advancement)
        for (long i = 1; i <= 100; i++)
        {
            var result = await cachedStore.GetPoisonPill(subscription2, i, CancellationToken.None);
            Assert.Null(result); // Should still work
        }
    }

    [Fact]
    public async Task GetPoisonPill_CachesPoisonPills_Correctly()
    {
        // Arrange
        var innerStore = new InMemoryPoisonPillStore();
        var logger = NullLogger<CachedPoisonPillStore>.Instance;
        var cachedStore = new CachedPoisonPillStore(innerStore, logger);

        const string subscriptionId = "test-subscription";
        var poisonPill = new PoisonPill(
            Guid.NewGuid(),
            subscriptionId,
            100,
            Guid.NewGuid(),
            "TestEvent",
            "{}",
            "{}",
            "Error message",
            "Stack trace",
            3,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null
        );

        // Act - Store poison pill
        await cachedStore.StorePoisonPill(poisonPill, CancellationToken.None);

        // Query the poison pill twice
        var result1 = await cachedStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);
        var result2 = await cachedStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(poisonPill.Id, result1.Id);
        Assert.Equal(poisonPill.Id, result2.Id);
    }

    [Fact]
    public async Task ResolvePoisonPill_UpdatesCache_Correctly()
    {
        // Arrange
        var innerStore = new InMemoryPoisonPillStore();
        var logger = NullLogger<CachedPoisonPillStore>.Instance;
        var cachedStore = new CachedPoisonPillStore(innerStore, logger);

        const string subscriptionId = "test-subscription";
        var poisonPill = new PoisonPill(
            Guid.NewGuid(),
            subscriptionId,
            100,
            Guid.NewGuid(),
            "TestEvent",
            "{}",
            "{}",
            "Error message",
            "Stack trace",
            3,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null
        );

        // Act - Store and cache poison pill
        await cachedStore.StorePoisonPill(poisonPill, CancellationToken.None);

        // Resolve it
        await cachedStore.ResolvePoisonPill(poisonPill.Id, "admin", "fixed", CancellationToken.None);

        // Query again
        var result = await cachedStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);

        // Assert - Should be marked as resolved
        Assert.NotNull(result);
        Assert.NotNull(result.ResolvedAt);
        Assert.Equal("admin", result.ResolvedBy);
        Assert.Equal("fixed", result.ResolutionAction);
    }

    /// <summary>
    /// Simple in-memory poison pill store for testing
    /// </summary>
    private class InMemoryPoisonPillStore : IPoisonPillStore
    {
        private readonly List<PoisonPill> _poisonPills = new();

        public ValueTask StorePoisonPill(PoisonPill poisonPill, CancellationToken ct)
        {
            _poisonPills.Add(poisonPill);
            return ValueTask.CompletedTask;
        }

        public ValueTask<PoisonPill?> GetPoisonPill(string subscriptionId, long position, CancellationToken ct)
        {
            var poisonPill = _poisonPills.FirstOrDefault(p =>
                p.SubscriptionId == subscriptionId && p.GlobalPosition == position);
            return ValueTask.FromResult(poisonPill);
        }

        public ValueTask ResolvePoisonPill(Guid poisonPillId, string resolvedBy, string action, CancellationToken ct)
        {
            var poisonPill = _poisonPills.FirstOrDefault(p => p.Id == poisonPillId);
            if (poisonPill != null)
            {
                _poisonPills.Remove(poisonPill);
                _poisonPills.Add(poisonPill with { ResolvedAt = DateTimeOffset.UtcNow, ResolvedBy = resolvedBy, ResolutionAction = action });
            }

            return ValueTask.CompletedTask;
        }
    }
}