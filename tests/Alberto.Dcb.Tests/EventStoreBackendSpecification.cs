using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Specification tests for IEventStoreBackend implementations.
/// These tests define the contract that all implementations must follow.
/// </summary>
public abstract class EventStoreBackendSpecification
{
    /// <summary>
    /// Unique tenant ID generated per test instance for isolation.
    /// </summary>
    protected string Tenant { get; } = $"test-{Guid.NewGuid():N}";

    protected FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero));

    /// <summary>
    /// Factory method to create the backend under test.
    /// </summary>
    protected abstract Task<IEventStoreBackend> CreateBackend();

    #region Append Tests

    [Fact]
    public async Task Append_SingleEvent_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var eventToPersist = CreateEvent("order-placed", "order:123");

        var result = await backend.Append(Tenant, [eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(eventToPersist.Id, result.First().Id);
        Assert.Equal(eventToPersist.EventType.Id, result.First().EventType.Id);    }

    [Fact]
    public async Task Append_MultipleEvents_ShouldAssignIncreasingPositions()
    {
        var backend = await CreateBackend();
        var events = new[]
        {
            CreateEvent("event-a", "order:123"),
            CreateEvent("event-b", "order:123"),
            CreateEvent("event-c", "order:123")
        };

        var result = await backend.Append(Tenant, events, cancellationToken: TestContext.Current.CancellationToken);

        var positions = result.Select(e => e.GlobalPosition).ToList();
        Assert.Equal(3, positions.Count);
        Assert.True(positions[0] < positions[1] && positions[1] < positions[2]);    }

    [Fact]
    public async Task Append_ShouldPreserveEventData()
    {
        var backend = await CreateBackend();
        var eventToPersist = CreateEvent("order-placed", "order:123");

        var result = await backend.Append(Tenant, [eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

        var appended = result.First();
        Assert.Equal(eventToPersist.EventData, appended.EventData);
        Assert.Equal(eventToPersist.Tags.Count, appended.Tags.Count);    }

    [Fact]
    public async Task Append_ShouldPreserveMetadata()
    {
        var backend = await CreateBackend();
        var metadata = new Dictionary<string, string>
        {
            ["correlation-id"] = "corr-123",
            ["user-id"] = "user-456"
        };
        var eventToPersist = CreateEvent("order-placed", metadata, "order:123");

        var result = await backend.Append(Tenant, [eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

        var appended = result.First();
        Assert.Equal("corr-123", appended.Metadata["correlation-id"]);
        Assert.Equal("user-456", appended.Metadata["user-id"]);    }

    #endregion

    #region Stream Tests

    [Fact]
    public async Task Stream_EmptyStore_ShouldReturnEmpty()
    {
        var backend = await CreateBackend();
        var query = DcbQuery.ByTags("order:123");

        var result = await backend.Stream(Tenant, query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result);    }

    [Fact]
    public async Task Stream_ByTags_ShouldReturnMatchingEvents()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("order-placed", "order:123"),
            CreateEvent("order-confirmed", "order:123"),
            CreateEvent("customer-updated", "customer:456")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(Tenant, DcbQuery.ByTags("order:123"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Contains(e.Tags, t => t.Value == "order:123"));    }

    [Fact]
    public async Task Stream_ByTypes_ShouldReturnMatchingEvents()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("order-placed", "order:123"),
            CreateEvent("order-confirmed", "order:123"),
            CreateEvent("order-placed", "order:456")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(Tenant, DcbQuery.ByTypes("order-placed"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("order-placed", e.EventType.Id));    }

    [Fact]
    public async Task Stream_ByTypesOrTags_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("order-placed", "order:123"),      // matches type
            CreateEvent("order-confirmed", "order:456"),   // matches tag
            CreateEvent("customer-updated", "customer:789") // matches neither
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes("order-placed")
            .WithTags(new EventTag("order", "456"));

        var result = await backend.Stream(Tenant, query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);    }

    [Fact]
    public async Task Stream_ByAllTags_ShouldRequireAllTagsToMatch()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("source-followed", "reader:123", "source:456"),
            CreateEvent("source-unfollowed", "reader:123"),
            CreateEvent("source-followed", "source:456")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(
            Tenant,
            DcbQuery.ByAllTags("reader:123", "source:456"),
            cancellationToken: TestContext.Current.CancellationToken);

        var matched = Assert.Single(result);
        Assert.Equal("source-followed", matched.EventType.Id);
        Assert.Contains(matched.Tags, tag => tag.Value == "reader:123");
        Assert.Contains(matched.Tags, tag => tag.Value == "source:456");
    }

    [Fact]
    public async Task Stream_EmptyQuery_ShouldReturnAllTenantEvents()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("event-a", "tag:1"),
            CreateEvent("event-b", "tag:2"),
            CreateEvent("event-c", "tag:3")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(Tenant, DcbQuery.Empty, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);    }

    [Fact]
    public async Task Stream_WithAfterPosition_ShouldFilterByPosition()
    {
        var backend = await CreateBackend();
        var firstBatch = await backend.Append(Tenant, [
            CreateEvent("event-a", "order:123"),
            CreateEvent("event-b", "order:123")
        ], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append(Tenant, [
            CreateEvent("event-c", "order:123")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var afterPosition = firstBatch.Last().GlobalPosition;
        var result = await backend.Stream(Tenant, DcbQuery.ByTags("order:123"), afterPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("event-c", result.First().EventType.Id);    }

    [Fact]
    public async Task Stream_WithLimit_ShouldLimitResults()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("event-a", "order:123"),
            CreateEvent("event-b", "order:123"),
            CreateEvent("event-c", "order:123"),
            CreateEvent("event-d", "order:123"),
            CreateEvent("event-e", "order:123")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(Tenant, DcbQuery.ByTags("order:123"), limit: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);    }

    [Fact]
    public async Task Stream_ShouldOrderByPosition()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("event-c", "order:123"),
            CreateEvent("event-a", "order:123"),
            CreateEvent("event-b", "order:123")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(Tenant, DcbQuery.ByTags("order:123"), cancellationToken: TestContext.Current.CancellationToken);

        var positions = result.Select(e => e.GlobalPosition).ToList();
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p)));    }

    #endregion

    #region Wildcard Tag Query Tests

    [Fact]
    public async Task Stream_ByTagPrefix_ShouldReturnAllMatchingTags()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("order-placed", "order:123"),
            CreateEvent("order-confirmed", "order:456"),
            CreateEvent("order-shipped", "order:789"),
            CreateEvent("customer-updated", "customer:111")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(Tenant, DcbQuery.ByTagPatterns("order:*"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.All(result, e => Assert.Contains(e.Tags, t => t.Concept == "order"));
    }

    [Fact]
    public async Task Stream_ByMultipleTagPrefixes_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("order-placed", "order:123"),
            CreateEvent("customer-created", "customer:456"),
            CreateEvent("product-updated", "product:789")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.ByTagPatterns("order:*", "customer:*");
        var result = await backend.Stream(Tenant, query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Stream_ByMixedExactAndWildcardTags_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("order-placed", "order:123"),
            CreateEvent("order-confirmed", "order:456"),
            CreateEvent("customer-created", "customer:789"),
            CreateEvent("product-updated", "product:111")
        ], cancellationToken: TestContext.Current.CancellationToken);

        // Mix exact tag with wildcard pattern
        var query = DcbQuery.Empty
            .WithTags("product:111")
            .WithTagPatterns("order:*");

        var result = await backend.Stream(Tenant, query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Stream_ByTypesAndWildcardTags_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [
            CreateEvent("order-placed", "order:123"),         // matches type
            CreateEvent("order-confirmed", "order:456"),      // matches tag wildcard
            CreateEvent("customer-created", "customer:789"),  // matches nothing
            CreateEvent("order-placed", "product:111")        // matches type
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes("customer-created")
            .WithTagPatterns("order:*");

        var result = await backend.Stream(Tenant, query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Stream_ByTagPrefix_WithAfterPosition_ShouldFilter()
    {
        var backend = await CreateBackend();
        var firstBatch = await backend.Append(Tenant, [
            CreateEvent("order-placed", "order:123"),
            CreateEvent("order-confirmed", "order:456")
        ], cancellationToken: TestContext.Current.CancellationToken);

        await backend.Append(Tenant, [
            CreateEvent("order-shipped", "order:789")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var afterPosition = firstBatch.Last().GlobalPosition;
        var result = await backend.Stream(Tenant, DcbQuery.ByTagPatterns("order:*"), afterPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("order-shipped", result.First().EventType.Id);
    }

    [Fact]
    public async Task Append_WithDcbCheck_WildcardPattern_NoConflict_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(Tenant, [CreateEvent("order-placed", "order:123")], cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        // DCB check with wildcard: any order tag
        var dcbQuery = DcbQuery.ByTagPatterns("order:*");
        var result = await backend.Append(Tenant, [CreateEvent("order-confirmed", "order:456")], dcbQuery, lastPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    [Fact]
    public async Task Append_WithDcbCheck_WildcardPattern_WithConflict_ShouldThrow()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(Tenant, [CreateEvent("order-placed", "order:123")], cancellationToken: TestContext.Current.CancellationToken);
        var firstPosition = initial.First().GlobalPosition;

        // Add a conflicting event with a different order ID
        await backend.Append(Tenant, [CreateEvent("order-confirmed", "order:456")], cancellationToken: TestContext.Current.CancellationToken);

        // DCB check with wildcard: should detect conflict on any order:* tag
        var dcbQuery = DcbQuery.ByTagPatterns("order:*");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(Tenant, [CreateEvent("order-shipped", "order:789")], dcbQuery, firstPosition, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Append_WithDcbCheck_WildcardPattern_DifferentConcept_ShouldNotConflict()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [CreateEvent("order-placed", "order:123")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append(Tenant, [CreateEvent("customer-updated", "customer:456")], cancellationToken: TestContext.Current.CancellationToken);

        // DCB check only on customer wildcard, not order
        var dcbQuery = DcbQuery.ByTagPatterns("customer:*");
        var lastCustomerPosition = (await backend.Stream(Tenant, dcbQuery, cancellationToken: TestContext.Current.CancellationToken)).Last().GlobalPosition;

        var result = await backend.Append(Tenant, [CreateEvent("customer-verified", "customer:789")], dcbQuery, lastCustomerPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    #endregion

    #region Tenant Isolation Tests

    [Fact]
    public async Task Stream_ShouldIsolateTenants()
    {
        var backend = await CreateBackend();
        var tenantA = $"{Tenant}-a";
        var tenantB = $"{Tenant}-b";

        await backend.Append(tenantA, [CreateEvent("event-a", "order:123")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append(tenantB, [CreateEvent("event-b", "order:123")], cancellationToken: TestContext.Current.CancellationToken);

        var resultA = await backend.Stream(tenantA, DcbQuery.ByTags("order:123"), cancellationToken: TestContext.Current.CancellationToken);
        var resultB = await backend.Stream(tenantB, DcbQuery.ByTags("order:123"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(resultA);
        Assert.Equal("event-a", resultA.First().EventType.Id);
        Assert.Single(resultB);
        Assert.Equal("event-b", resultB.First().EventType.Id);
    }

    [Fact]
    public async Task StreamGlobal_ShouldReturnAllTenantEvents()
    {
        var backend = await CreateBackend();
        var tenantA = $"{Tenant}-a";
        var tenantB = $"{Tenant}-b";

        // Get starting position to filter out events from other tests
        var startPosition = await backend.GetLastPositionGlobal(TestContext.Current.CancellationToken);

        await backend.Append(tenantA, [CreateEvent("event-a", "tag:1")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append(tenantB, [CreateEvent("event-b", "tag:2")], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamGlobal(afterPosition: startPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
    }

    #endregion

    #region DCB Consistency Tests

    [Fact]
    public async Task Append_WithDcbCheck_NoConflict_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(Tenant, [CreateEvent("order-placed", "order:123")], cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        var dcbQuery = DcbQuery.ByTags("order:123");
        var result = await backend.Append(Tenant, [CreateEvent("order-confirmed", "order:123")], dcbQuery, lastPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_WithConflict_ShouldThrow()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(Tenant, [CreateEvent("order-placed", "order:123")], cancellationToken: TestContext.Current.CancellationToken);
        var firstPosition = initial.First().GlobalPosition;

        // Add a conflicting event
        await backend.Append(Tenant, [CreateEvent("order-confirmed", "order:123")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags("order:123");

        // Try to append expecting only the first event
        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(Tenant, [CreateEvent("order-shipped", "order:123")], dcbQuery, firstPosition, TestContext.Current.CancellationToken));    }

    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithExisting_ShouldThrow()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [CreateEvent("order-placed", "order:123")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags("order:123");

        // Try to append expecting no events (position 0)
        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(Tenant, [CreateEvent("order-created", "order:123")], dcbQuery, 0, TestContext.Current.CancellationToken));    }

    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithNone_ShouldSucceed()
    {
        var backend = await CreateBackend();

        var dcbQuery = DcbQuery.ByTags("order:123");

        var result = await backend.Append(Tenant, [CreateEvent("order-created", "order:123")], dcbQuery, 0, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_DifferentBoundary_ShouldNotConflict()
    {
        var backend = await CreateBackend();
        await backend.Append(Tenant, [CreateEvent("order-placed", "order:123")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append(Tenant, [CreateEvent("customer-updated", "customer:456")], cancellationToken: TestContext.Current.CancellationToken);

        // DCB check only on customer boundary, not order
        var dcbQuery = DcbQuery.ByTags("customer:456");
        var lastCustomerPosition = (await backend.Stream(Tenant, dcbQuery, cancellationToken: TestContext.Current.CancellationToken)).Last().GlobalPosition;

        var result = await backend.Append(Tenant, [CreateEvent("customer-verified", "customer:456")], dcbQuery, lastCustomerPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_AllTags_ShouldIgnoreEventsMatchingOnlyOneTag()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(
            Tenant,
            [CreateEvent("source-followed", "reader:123", "source:456")],
            cancellationToken: TestContext.Current.CancellationToken);

        var lastPosition = initial.Last().GlobalPosition;

        await backend.Append(
            Tenant,
            [CreateEvent("reader-profile-updated", "reader:123")],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Append(
            Tenant,
            [CreateEvent("source-followed", "reader:123", "source:456")],
            DcbQuery.ByAllTags("reader:123", "source:456"),
            lastPosition,
            TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    [Fact]
    public async Task Append_WithDcbCheck_AllTags_ShouldDetectConflictsForMatchingTagSet()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(
            Tenant,
            [CreateEvent("source-followed", "reader:123", "source:456")],
            cancellationToken: TestContext.Current.CancellationToken);

        var lastPosition = initial.Last().GlobalPosition;

        await backend.Append(
            Tenant,
            [CreateEvent("source-unfollowed", "reader:123", "source:456")],
            cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(
                Tenant,
                [CreateEvent("source-followed", "reader:123", "source:456")],
                DcbQuery.ByAllTags("reader:123", "source:456"),
                lastPosition,
                TestContext.Current.CancellationToken));
    }

    #endregion

    #region Position Tests

    [Fact]
    public async Task GetLastPosition_EmptyTenant_ShouldReturnZero()
    {
        var backend = await CreateBackend();

        var position = await backend.GetLastPosition(Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(0, position);    }

    [Fact]
    public async Task GetLastPosition_WithEvents_ShouldReturnLastPosition()
    {
        var backend = await CreateBackend();
        var appended = await backend.Append(Tenant, [
            CreateEvent("event-a", "tag:1"),
            CreateEvent("event-b", "tag:2")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var position = await backend.GetLastPosition(Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(appended.Last().GlobalPosition, position);    }

    [Fact]
    public async Task GetLastPositionGlobal_ShouldReturnGlobalMax()
    {
        var backend = await CreateBackend();
        var tenantA = $"{Tenant}-a";
        var tenantB = $"{Tenant}-b";

        await backend.Append(tenantA, [CreateEvent("event-a", "tag:1")], cancellationToken: TestContext.Current.CancellationToken);
        var lastAppend = await backend.Append(tenantB, [CreateEvent("event-b", "tag:2")], cancellationToken: TestContext.Current.CancellationToken);

        var position = await backend.GetLastPositionGlobal(TestContext.Current.CancellationToken);

        // Position should be at least our last appended event (may be higher if other tests run in parallel)
        Assert.True(position >= lastAppend.Last().GlobalPosition);
    }

    #endregion

    #region Helper Methods

    protected IEventToPersist CreateEvent(string eventType, params string[] tags)
        => CreateEvent(eventType, new Dictionary<string, string>(), tags);

    protected IEventToPersist CreateEvent(string eventType, Dictionary<string, string> metadata, params string[] tags)
    {
        return new EventToPersist
        {
            TenantId = Tenant,
            EventType = new EventType(eventType),
            EventData = """{"test": true}""",
            Tags = tags.Select(EventTag.Parse).ToArray(),
            Metadata = metadata
        };
    }

    #endregion
}
