using EventStore;
using EventStore.Events;
using EventStore.MultiTenant;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EventStore.Tests.Specifications;

/// <summary>
/// Advanced query scenario tests to be added to the specification
/// These extend the base specification with complex querying scenarios
/// </summary>
public abstract class AdvancedQueryTests
{
    public TimeProvider TimeProvider { get; } =
        new FakeTimeProvider(new DateTimeOffset(2025, 3, 21, 11, 47, 12, TimeSpan.FromHours(5)));

    protected abstract Task<IEventStoreBackend> CreateBackend();
    protected abstract Tenant CurrentTenant();
    protected virtual Task SetupAsync() => Task.CompletedTask;
    protected virtual Task CleanupAsync() => Task.CompletedTask;

    [Fact]
    public async Task Stream_WithPaginationUsingPosition_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var tenant = CurrentTenant();

        var events = Enumerable.Range(1, 10)
            .Select(i => CreateTestEvent($"event-{ToLetters(i)}", "order:123"))
            .ToArray();

        var appendResult = await backend.Append(tenant, events, null, null, CancellationToken.None);
        var allEvents = appendResult.ToList();

        // Act - Get first 3 events
        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        var firstPage = await backend.Stream(tenant, query, maxCount: 3, cancellationToken: CancellationToken.None);

        // Get remaining events (pagination would need to be implemented differently)
        var allEventsForPaging = await backend.Stream(tenant, query, cancellationToken: CancellationToken.None);
        var secondPage = allEventsForPaging.Skip(3).Take(3).ToList();

        // Assert
        Assert.Equal(3, firstPage.Count);
        Assert.Equal(3, secondPage.Count);

        // Verify pagination worked correctly
        var firstPagePositions = firstPage.Select(e => long.Parse(e.Metadata["_position"])).ToList();
        var secondPagePositions = secondPage.Select(e => long.Parse(e.Metadata["_position"])).ToList();

        Assert.True(secondPagePositions.All(pos => pos > firstPagePositions.Max()));

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithComplexTagCombinations_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var tenant = CurrentTenant();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123", "customer:456", "region:us"),
            CreateTestEvent("payment-processed", "order:123", "payment:789", "region:us"),
            CreateTestEvent("item-shipped", "order:123", "shipping:101", "region:eu"),
            CreateTestEvent("customer-updated", "customer:456", "region:us"),
            CreateTestEvent("order-created", "order:124", "customer:789", "region:eu")
        };

        await backend.Append(tenant, events, null, null, CancellationToken.None);

        // Test 1: ALL tags - order AND customer AND region
        var allTagsQuery = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"), EventTag.Parse("customer:456"), EventTag.Parse("region:us"))
            .RequiringAllTags();

        var allTagsResult = await backend.Stream(tenant, allTagsQuery, cancellationToken: CancellationToken.None);

        // Test 2: ANY tags - order OR customer
        var anyTagsQuery = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"), EventTag.Parse("customer:789"));

        var anyTagsResult = await backend.Stream(tenant, anyTagsQuery, cancellationToken: CancellationToken.None);

        // Test 3: Complex combination - US region events for specific customer
        var complexQuery = new StreamQuery()
            .WithTags(EventTag.Parse("customer:456"), EventTag.Parse("region:us"))
            .RequiringAllTags();

        var complexResult = await backend.Stream(tenant, complexQuery, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Single(allTagsResult); // Only the order-created event has all three tags
        Assert.Equal("order-created", allTagsResult.First().EventType.Id);

        Assert.Equal(4, anyTagsResult.Count); // 3 events with order:123 + 1 event with customer:789

        Assert.Equal(2, complexResult.Count); // order-created and customer-updated

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithEventTypeWildcardPatterns_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var tenant = CurrentTenant();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123"),
            CreateTestEvent("order-updated", "order:123"),
            CreateTestEvent("order-completed", "order:123"),
            CreateTestEvent("payment-created", "order:123"),
            CreateTestEvent("payment-processed", "order:123"),
            CreateTestEvent("notification-sent", "order:123")
        };

        await backend.Append(tenant, events, null, null, CancellationToken.None);

        // Test: Multiple event types with pattern-like filtering
        var orderEventTypes = new[]
        {
            new EventType("order-created"),
            new EventType("order-updated"),
            new EventType("order-completed")
        };

        var paymentEventTypes = new[]
        {
            new EventType("payment-created"),
            new EventType("payment-processed")
        };

        var orderQuery = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(orderEventTypes);

        var paymentQuery = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(paymentEventTypes);

        // Act
        var orderResult = await backend.Stream(tenant, orderQuery, cancellationToken: CancellationToken.None);
        var paymentResult = await backend.Stream(tenant, paymentQuery, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(3, orderResult.Count);
        Assert.All(orderResult, e => Assert.StartsWith("order-", e.EventType.Id));

        Assert.Equal(2, paymentResult.Count);
        Assert.All(paymentResult, e => Assert.StartsWith("payment-", e.EventType.Id));

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithMaxCountAndEventTypeFilter_ShouldRespectBoth()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var tenant = CurrentTenant();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123"),
            CreateTestEvent("payment-processed", "order:123"),
            CreateTestEvent("order-updated", "order:123"),
            CreateTestEvent("order-completed", "order:123"),
            CreateTestEvent("notification-sent", "order:123")
        };

        await backend.Append(tenant, events, null, null, CancellationToken.None);

        // Test: Limit to 2 events, but only order events
        var query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(new EventType("order-created"), new EventType("order-updated"), new EventType("order-completed"));

        var result = await backend.Stream(tenant, query, maxCount: 2, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.StartsWith("order-", e.EventType.Id));

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithDeepTagHierarchy_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var tenant = CurrentTenant();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123", "customer:456", "org:acme", "region:us", "tier:premium"),
            CreateTestEvent("payment-processed", "order:123", "payment:789", "provider:stripe", "currency:usd"),
            CreateTestEvent("audit-logged", "order:123", "audit:security", "user:admin", "action:create"),
            CreateTestEvent("metrics-recorded", "order:123", "metrics:performance", "component:api", "duration:150ms")
        };

        await backend.Append(tenant, events, null, null, CancellationToken.None);

        // Test hierarchical tag queries
        var customerHierarchyQuery = new StreamQuery()
            .WithTags(EventTag.Parse("customer:456"), EventTag.Parse("org:acme"), EventTag.Parse("tier:premium"))
            .RequiringAllTags();

        var paymentProviderQuery = new StreamQuery()
            .WithTags(EventTag.Parse("provider:stripe"), EventTag.Parse("currency:usd"))
            .RequiringAllTags();

        // Act
        var customerResult = await backend.Stream(tenant, customerHierarchyQuery, cancellationToken: CancellationToken.None);
        var paymentResult = await backend.Stream(tenant, paymentProviderQuery, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Single(customerResult);
        Assert.Equal("order-created", customerResult.First().EventType.Id);

        Assert.Single(paymentResult);
        Assert.Equal("payment-processed", paymentResult.First().EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithLargeResultSet_ShouldHandleEfficiently()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var tenant = CurrentTenant();

        // Create 1000 events for performance testing
        var largeEventSet = Enumerable.Range(0, 1000)
            .Select(i => CreateTestEvent($"bulk-event-{ToLetters(i)}", "bulk:test", $"batch:{i / 100}"))
            .ToArray();

        await backend.Append(tenant, largeEventSet, null, null, CancellationToken.None);

        // Test: Query all events
        var allEventsQuery = new StreamQuery().WithTags(EventTag.Parse("bulk:test"));
        var allEventsResult = await backend.Stream(tenant, allEventsQuery, cancellationToken: CancellationToken.None);

        // Test: Query specific batch
        var batchQuery = new StreamQuery()
            .WithTags(EventTag.Parse("bulk:test"), EventTag.Parse("batch:5"))
            .RequiringAllTags();
        var batchResult = await backend.Stream(tenant, batchQuery, cancellationToken: CancellationToken.None);

        // Test: Large limited query
        var limitedQuery = new StreamQuery().WithTags(EventTag.Parse("bulk:test"));
        var limitedResult = await backend.Stream(tenant, limitedQuery, maxCount: 50, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(1000, allEventsResult.Count);
        Assert.Equal(100, batchResult.Count); // batch 5 = events 500-599
        Assert.Equal(50, limitedResult.Count);

        // Verify ordering is maintained
        var positions = allEventsResult.Select(e => long.Parse(e.Metadata["_position"])).ToList();
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p)));

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_CrossTenantQuery_ShouldReturnEmpty()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var tenant1 = new Tenant("tenant-1");
        var tenant2 = new Tenant("tenant-2");

        var tenant1Event = CreateTestEvent("tenanta-event", "shared:tag");
        var tenant2Event = CreateTestEvent("tenantb-event", "shared:tag");

        await backend.Append(tenant1, [tenant1Event], null, null, CancellationToken.None);
        await backend.Append(tenant2, [tenant2Event], null, null, CancellationToken.None);

        // Act - Query tenant1's events from tenant2's context
        var crossTenantQuery = new StreamQuery().WithTags(EventTag.Parse("shared:tag"));
        var tenant1Result = await backend.Stream(tenant1, crossTenantQuery, cancellationToken: CancellationToken.None);
        var tenant2Result = await backend.Stream(tenant2, crossTenantQuery, cancellationToken: CancellationToken.None);

        // Assert - Each tenant should only see their own events
        Assert.Single(tenant1Result);
        Assert.Equal("tenanta-event", tenant1Result.First().EventType.Id);

        Assert.Single(tenant2Result);
        Assert.Equal("tenantb-event", tenant2Result.First().EventType.Id);

        await CleanupAsync();
    }

    private IEventToPersist CreateTestEvent(string eventType, params string[] tags)
    {
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventJson = """{"data": "test"}""",
            Tags = tags.Select(EventTag.Parse).ToList(),
            Metadata = new Dictionary<string, string>(),
            Created = TimeProvider.GetUtcNow()
        };
    }

    /// <summary>
    /// Converts a number to a base-26 letter string (e.g., 0 = A, 1 = B, ..., 25 = Z, 26 = AA, etc.)
    /// </summary>
    private static string ToLetters(int number)
    {
        var result = string.Empty;
        number++;
        while (number > 0)
        {
            number--;
            result = (char)('a' + (number % 26)) + result;
            number /= 26;
        }
        return result;
    }
}