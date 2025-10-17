using Alberto.EventStore.Subscriptions.PoisonPills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alberto.EventStore.Tests.Subscriptions;

/// <summary>
/// Tests for CachedPoisonPillStore write-through cache with lazy initialization
/// </summary>
public class CachedPoisonPillStoreTests
{
    [Fact]
    public async Task GetPoisonPill_InitializesLazily_OnFirstAccess()
    {
        // Arrange
        var innerStore = new InMemoryPoisonPillStore();

        // Pre-populate inner store with existing poison pills
        var existingPill = new PoisonPill(
            Guid.NewGuid(),
            "subscription-1",
            50,
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
        await innerStore.StorePoisonPill(existingPill, CancellationToken.None);

        var logger = NullLogger<CachedPoisonPillStore>.Instance;
        var cachedStore = new CachedPoisonPillStore(innerStore, logger);

        // Act - First access should trigger initialization and load existing pills
        var result = await cachedStore.GetPoisonPill("subscription-1", 50, CancellationToken.None);

        // Assert - Should find the pre-existing poison pill
        Assert.NotNull(result);
        Assert.Equal(existingPill.Id, result.Id);

        // Subsequent queries should return from in-memory cache (no DB hit)
        var result2 = await cachedStore.GetPoisonPill("subscription-1", 50, CancellationToken.None);
        Assert.NotNull(result2);
        Assert.Equal(existingPill.Id, result2.Id);
    }

    [Fact]
    public async Task GetPoisonPill_ReturnsNull_WhenNoPoisonPillExists()
    {
        // Arrange
        var innerStore = new InMemoryPoisonPillStore();
        var logger = NullLogger<CachedPoisonPillStore>.Instance;
        var cachedStore = new CachedPoisonPillStore(innerStore, logger);

        // Act
        var result = await cachedStore.GetPoisonPill("subscription-1", 100, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task StorePoisonPill_WritesToMemoryAndDatabase()
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

        // Assert - Should be retrievable from cache
        var cachedResult = await cachedStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);
        Assert.NotNull(cachedResult);
        Assert.Equal(poisonPill.Id, cachedResult.Id);

        // Assert - Should also be in inner store (write-through)
        var innerResult = await innerStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);
        Assert.NotNull(innerResult);
        Assert.Equal(poisonPill.Id, innerResult.Id);
    }

    [Fact]
    public async Task StorePoisonPill_CanBeQueriedMultipleTimes()
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

        // Query multiple times (should all return from in-memory cache)
        var result1 = await cachedStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);
        var result2 = await cachedStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);
        var result3 = await cachedStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);

        // Assert - All queries should return the same poison pill
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotNull(result3);
        Assert.Equal(poisonPill.Id, result1.Id);
        Assert.Equal(poisonPill.Id, result2.Id);
        Assert.Equal(poisonPill.Id, result3.Id);
    }

    [Fact]
    public async Task ResolvePoisonPill_UpdatesMemoryAndDatabase()
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

        // Resolve it
        await cachedStore.ResolvePoisonPill(poisonPill.Id, "admin", "fixed", CancellationToken.None);

        // Query from cache
        var cachedResult = await cachedStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);

        // Assert - Should be marked as resolved in cache
        Assert.NotNull(cachedResult);
        Assert.NotNull(cachedResult.ResolvedAt);
        Assert.Equal("admin", cachedResult.ResolvedBy);
        Assert.Equal("fixed", cachedResult.ResolutionAction);

        // Assert - Should also be resolved in inner store (write-through)
        var innerResult = await innerStore.GetPoisonPill(subscriptionId, 100, CancellationToken.None);
        Assert.NotNull(innerResult);
        Assert.NotNull(innerResult.ResolvedAt);
        Assert.Equal("admin", innerResult.ResolvedBy);
        Assert.Equal("fixed", innerResult.ResolutionAction);
    }

    [Fact]
    public async Task GetAllPoisonPills_ReturnsAllFromCache()
    {
        // Arrange
        var innerStore = new InMemoryPoisonPillStore();
        var logger = NullLogger<CachedPoisonPillStore>.Instance;
        var cachedStore = new CachedPoisonPillStore(innerStore, logger);

        // Store multiple poison pills
        var pill1 = new PoisonPill(
            Guid.NewGuid(), "sub-1", 10, Guid.NewGuid(), "Event1", "{}", "{}", "Error 1", null, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null
        );
        var pill2 = new PoisonPill(
            Guid.NewGuid(), "sub-2", 20, Guid.NewGuid(), "Event2", "{}", "{}", "Error 2", null, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null
        );

        await cachedStore.StorePoisonPill(pill1, CancellationToken.None);
        await cachedStore.StorePoisonPill(pill2, CancellationToken.None);

        // Act
        var allPills = await cachedStore.GetAllPoisonPills(CancellationToken.None);

        // Assert
        Assert.Equal(2, allPills.Count);
        Assert.Contains(allPills, p => p.Id == pill1.Id);
        Assert.Contains(allPills, p => p.Id == pill2.Id);
    }

    [Fact]
    public async Task MultipleSubscriptions_IsolatedCorrectly()
    {
        // Arrange
        var innerStore = new InMemoryPoisonPillStore();
        var logger = NullLogger<CachedPoisonPillStore>.Instance;
        var cachedStore = new CachedPoisonPillStore(innerStore, logger);

        // Store poison pills for different subscriptions
        var pill1 = new PoisonPill(
            Guid.NewGuid(), "subscription-1", 100, Guid.NewGuid(), "Event", "{}", "{}", "Error", null, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null
        );
        var pill2 = new PoisonPill(
            Guid.NewGuid(), "subscription-2", 100, Guid.NewGuid(), "Event", "{}", "{}", "Error", null, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null
        );

        await cachedStore.StorePoisonPill(pill1, CancellationToken.None);
        await cachedStore.StorePoisonPill(pill2, CancellationToken.None);

        // Act - Query for each subscription
        var result1 = await cachedStore.GetPoisonPill("subscription-1", 100, CancellationToken.None);
        var result2 = await cachedStore.GetPoisonPill("subscription-2", 100, CancellationToken.None);

        // Assert - Each subscription should get its own poison pill
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(pill1.Id, result1.Id);
        Assert.Equal(pill2.Id, result2.Id);
        Assert.NotEqual(result1.Id, result2.Id);
    }

    /// <summary>
    /// Simple in-memory poison pill store for testing
    /// </summary>
    private class InMemoryPoisonPillStore : IPoisonPillStore
    {
        private readonly List<PoisonPill> _poisonPills = [];

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

        public ValueTask<IReadOnlyList<PoisonPill>> GetAllPoisonPills(CancellationToken ct)
        {
            return ValueTask.FromResult<IReadOnlyList<PoisonPill>>(_poisonPills.ToList());
        }
    }
}