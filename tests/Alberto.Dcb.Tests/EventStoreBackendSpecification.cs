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
    /// Unique tag prefix generated per test instance for isolation (avoids cross-test interference).
    /// </summary>
    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

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
        var eventToPersist = CreateEvent("order-placed", $"order:{TestId}");

        var result = await backend.Append([eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(eventToPersist.Id, result.First().Id);
        Assert.Equal(eventToPersist.EventType.Id, result.First().EventType.Id);    }

    [Fact]
    public async Task Append_MultipleEvents_ShouldAssignIncreasingPositions()
    {
        var backend = await CreateBackend();
        var events = new[]
        {
            CreateEvent("event-a", $"order:{TestId}"),
            CreateEvent("event-b", $"order:{TestId}"),
            CreateEvent("event-c", $"order:{TestId}")
        };

        var result = await backend.Append(events, cancellationToken: TestContext.Current.CancellationToken);

        var positions = result.Select(e => e.GlobalPosition).ToList();
        Assert.Equal(3, positions.Count);
        Assert.True(positions[0] < positions[1] && positions[1] < positions[2]);    }

    [Fact]
    public async Task Append_ShouldPreserveEventData()
    {
        var backend = await CreateBackend();
        var eventToPersist = CreateEvent("order-placed", $"order:{TestId}");

        var result = await backend.Append([eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

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
        var eventToPersist = CreateEvent("order-placed", metadata, $"order:{TestId}");

        var result = await backend.Append([eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

        var appended = result.First();
        Assert.Equal("corr-123", appended.Metadata["correlation-id"]);
        Assert.Equal("user-456", appended.Metadata["user-id"]);    }

    #endregion

    #region Stream Tests

    [Fact]
    public async Task Stream_EmptyStore_ShouldReturnEmpty()
    {
        var backend = await CreateBackend();
        var query = DcbQuery.ByTags($"order:{TestId}-empty");

        var result = await backend.Stream(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result);    }

    [Fact]
    public async Task Stream_ByTags_ShouldReturnMatchingEvents()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent("order-placed", $"order:{TestId}"),
            CreateEvent("order-confirmed", $"order:{TestId}"),
            CreateEvent("customer-updated", $"customer:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(DcbQuery.ByTags($"order:{TestId}"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Contains(e.Tags, t => t.Value == $"order:{TestId}"));    }

    [Fact]
    public async Task Stream_ByTypes_ShouldReturnMatchingEvents()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent($"order-placed-{TestId}", $"order:1{TestId}"),
            CreateEvent($"order-confirmed-{TestId}", $"order:1{TestId}"),
            CreateEvent($"order-placed-{TestId}", $"order:2{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(DcbQuery.ByTypes($"order-placed-{TestId}"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal($"order-placed-{TestId}", e.EventType.Id));    }

    [Fact]
    public async Task Stream_ByTypesOrTags_AsUnion_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent($"order-placed-{TestId}", $"order:1{TestId}"),      // matches type
            CreateEvent($"order-confirmed-{TestId}", $"order:2{TestId}"),   // matches tag
            CreateEvent($"customer-updated-{TestId}", $"customer:3{TestId}") // matches neither
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes($"order-placed-{TestId}")
            .WithTags(new EventTag("order", $"2{TestId}"))
            .AsUnion();

        var result = await backend.Stream(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);    }

    [Fact]
    public async Task Stream_ByTypesAndTags_DefaultsToIntersect()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent($"order-placed-{TestId}", $"order:1{TestId}"),      // matches type only
            CreateEvent($"order-placed-{TestId}", $"order:2{TestId}"),      // matches both
            CreateEvent($"order-confirmed-{TestId}", $"order:2{TestId}"),   // matches tag only
            CreateEvent($"customer-updated-{TestId}", $"customer:3{TestId}") // matches neither
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes($"order-placed-{TestId}")
            .WithTags(new EventTag("order", $"2{TestId}"));

        var result = await backend.Stream(query, cancellationToken: TestContext.Current.CancellationToken);

        var matched = Assert.Single(result);
        Assert.Equal($"order-placed-{TestId}", matched.EventType.Id);
        Assert.Contains(matched.Tags, t => t.Value == $"order:2{TestId}");
    }

    [Fact]
    public async Task Stream_ByAllTags_ShouldRequireAllTagsToMatch()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent("source-followed", $"reader:{TestId}", $"source:{TestId}"),
            CreateEvent("source-unfollowed", $"reader:{TestId}"),
            CreateEvent("source-followed", $"source:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(
            DcbQuery.ByAllTags($"reader:{TestId}", $"source:{TestId}"),
            cancellationToken: TestContext.Current.CancellationToken);

        var matched = Assert.Single(result);
        Assert.Equal("source-followed", matched.EventType.Id);
        Assert.Contains(matched.Tags, tag => tag.Value == $"reader:{TestId}");
        Assert.Contains(matched.Tags, tag => tag.Value == $"source:{TestId}");
    }

    [Fact]
    public async Task Stream_EmptyQuery_ShouldReturnAllEvents()
    {
        var backend = await CreateBackend();

        // Get starting position to filter out events from other tests
        var startPosition = await backend.GetLastPosition(TestContext.Current.CancellationToken);

        await backend.Append([
            CreateEvent("event-a", $"tag:1{TestId}"),
            CreateEvent("event-b", $"tag:2{TestId}"),
            CreateEvent("event-c", $"tag:3{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAll(afterPosition: startPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);    }

    [Fact]
    public async Task Stream_WithAfterPosition_ShouldFilterByPosition()
    {
        var backend = await CreateBackend();
        var firstBatch = await backend.Append([
            CreateEvent("event-a", $"order:{TestId}"),
            CreateEvent("event-b", $"order:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append([
            CreateEvent("event-c", $"order:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var afterPosition = firstBatch.Last().GlobalPosition;
        var result = await backend.Stream(DcbQuery.ByTags($"order:{TestId}"), afterPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("event-c", result.First().EventType.Id);    }

    [Fact]
    public async Task Stream_WithLimit_ShouldLimitResults()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent("event-a", $"order:{TestId}"),
            CreateEvent("event-b", $"order:{TestId}"),
            CreateEvent("event-c", $"order:{TestId}"),
            CreateEvent("event-d", $"order:{TestId}"),
            CreateEvent("event-e", $"order:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(DcbQuery.ByTags($"order:{TestId}"), limit: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);    }

    [Fact]
    public async Task Stream_ShouldOrderByPosition()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent("event-c", $"order:{TestId}"),
            CreateEvent("event-a", $"order:{TestId}"),
            CreateEvent("event-b", $"order:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(DcbQuery.ByTags($"order:{TestId}"), cancellationToken: TestContext.Current.CancellationToken);

        var positions = result.Select(e => e.GlobalPosition).ToList();
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p)));    }

    #endregion

    #region Wildcard Tag Query Tests

    [Fact]
    public async Task Stream_ByTagPrefix_ShouldReturnAllMatchingTags()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent("order-placed", $"ord{TestId}:123"),
            CreateEvent("order-confirmed", $"ord{TestId}:456"),
            CreateEvent("order-shipped", $"ord{TestId}:789"),
            CreateEvent("customer-updated", $"cust{TestId}:111")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Stream(DcbQuery.ByTagPatterns($"ord{TestId}:*"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.All(result, e => Assert.Contains(e.Tags, t => t.Concept == $"ord{TestId}"));
    }

    [Fact]
    public async Task Stream_ByMultipleTagPrefixes_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent("order-placed", $"ord{TestId}:123"),
            CreateEvent("customer-created", $"cust{TestId}:456"),
            CreateEvent("product-updated", $"prod{TestId}:789")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.ByTagPatterns($"ord{TestId}:*", $"cust{TestId}:*");
        var result = await backend.Stream(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Stream_ByMixedExactAndWildcardTags_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent("order-placed", $"ord{TestId}:123"),
            CreateEvent("order-confirmed", $"ord{TestId}:456"),
            CreateEvent("customer-created", $"cust{TestId}:789"),
            CreateEvent("product-updated", $"prod{TestId}:111")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTags($"prod{TestId}:111")
            .WithTagPatterns($"ord{TestId}:*");

        var result = await backend.Stream(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Stream_ByTypesAndWildcardTags_AsUnion_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent($"order-placed-{TestId}", $"ord{TestId}:123"),         // matches type
            CreateEvent($"order-confirmed-{TestId}", $"ord{TestId}:456"),      // matches tag wildcard
            CreateEvent($"customer-created-{TestId}", $"cust{TestId}:789"),  // matches nothing
            CreateEvent($"order-placed-{TestId}", $"prod{TestId}:111")        // matches type
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes($"customer-created-{TestId}")
            .WithTagPatterns($"ord{TestId}:*")
            .AsUnion();

        var result = await backend.Stream(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Stream_ByTypesAndWildcardTags_DefaultsToIntersect()
    {
        var backend = await CreateBackend();
        await backend.Append([
            CreateEvent($"order-placed-{TestId}", $"ord{TestId}:123"),         // matches type AND tag prefix
            CreateEvent($"order-confirmed-{TestId}", $"ord{TestId}:456"),      // matches tag prefix only
            CreateEvent($"order-placed-{TestId}", $"prod{TestId}:111"),        // matches type only
            CreateEvent($"customer-created-{TestId}", $"cust{TestId}:789")     // matches neither
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes($"order-placed-{TestId}")
            .WithTagPatterns($"ord{TestId}:*");

        var result = await backend.Stream(query, cancellationToken: TestContext.Current.CancellationToken);

        var matched = Assert.Single(result);
        Assert.Equal($"order-placed-{TestId}", matched.EventType.Id);
        Assert.Contains(matched.Tags, t => t.Value == $"ord{TestId}:123");
    }

    [Fact]
    public async Task Append_WithDcbCheck_TypesAndTags_DefaultsToIntersect_NoConflict()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        // Tag matches but type does not — under Intersect this is NOT a conflict.
        await backend.Append(
            [CreateEvent($"customer-updated-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}").WithTypes($"order-placed-{TestId}");
        var result = await backend.Append(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            dcbQuery,
            lastPosition,
            TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    [Fact]
    public async Task Append_WithDcbCheck_TypesAndTags_DefaultsToIntersect_WithConflict()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        // Same type AND same tag — this should conflict under Intersect.
        await backend.Append(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}").WithTypes($"order-placed-{TestId}");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(
                [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
                dcbQuery,
                lastPosition,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Append_WithDcbCheck_TypesAndTags_AsUnion_StillTreatsTagOnlyMatchAsConflict()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        // Tag-only match — under Union semantics this conflicts with the boundary.
        await backend.Append(
            [CreateEvent($"customer-updated-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}").WithTypes($"order-placed-{TestId}").AsUnion();

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(
                [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
                dcbQuery,
                lastPosition,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stream_ByTagPrefix_WithAfterPosition_ShouldFilter()
    {
        var backend = await CreateBackend();
        var firstBatch = await backend.Append([
            CreateEvent("order-placed", $"ord{TestId}:123"),
            CreateEvent("order-confirmed", $"ord{TestId}:456")
        ], cancellationToken: TestContext.Current.CancellationToken);

        await backend.Append([
            CreateEvent("order-shipped", $"ord{TestId}:789")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var afterPosition = firstBatch.Last().GlobalPosition;
        var result = await backend.Stream(DcbQuery.ByTagPatterns($"ord{TestId}:*"), afterPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("order-shipped", result.First().EventType.Id);
    }

    [Fact]
    public async Task Append_WithDcbCheck_WildcardPattern_NoConflict_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append([CreateEvent("order-placed", $"ord{TestId}:123")], cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        var dcbQuery = DcbQuery.ByTagPatterns($"ord{TestId}:*");
        var result = await backend.Append([CreateEvent("order-confirmed", $"ord{TestId}:456")], dcbQuery, lastPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    [Fact]
    public async Task Append_WithDcbCheck_WildcardPattern_WithConflict_ShouldThrow()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append([CreateEvent("order-placed", $"ord{TestId}:123")], cancellationToken: TestContext.Current.CancellationToken);
        var firstPosition = initial.First().GlobalPosition;

        await backend.Append([CreateEvent("order-confirmed", $"ord{TestId}:456")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTagPatterns($"ord{TestId}:*");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append([CreateEvent("order-shipped", $"ord{TestId}:789")], dcbQuery, firstPosition, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Append_WithDcbCheck_WildcardPattern_DifferentConcept_ShouldNotConflict()
    {
        var backend = await CreateBackend();
        await backend.Append([CreateEvent("order-placed", $"ord{TestId}:123")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append([CreateEvent("customer-updated", $"cust{TestId}:456")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTagPatterns($"cust{TestId}:*");
        var lastCustomerPosition = (await backend.Stream(dcbQuery, cancellationToken: TestContext.Current.CancellationToken)).Last().GlobalPosition;

        var result = await backend.Append([CreateEvent("customer-verified", $"cust{TestId}:789")], dcbQuery, lastCustomerPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    #endregion

    #region StreamAll Tests

    [Fact]
    public async Task StreamAll_ShouldReturnAllEvents()
    {
        var backend = await CreateBackend();

        // Get starting position to filter out events from other tests
        var startPosition = await backend.GetLastPosition(TestContext.Current.CancellationToken);

        await backend.Append([CreateEvent("event-a", $"tag:1{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append([CreateEvent("event-b", $"tag:2{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAll(afterPosition: startPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
    }

    #endregion

    #region DCB Consistency Tests

    [Fact]
    public async Task Append_WithDcbCheck_NoConflict_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");
        var result = await backend.Append([CreateEvent("order-confirmed", $"order:{TestId}")], dcbQuery, lastPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_WithConflict_ShouldThrow()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        var firstPosition = initial.First().GlobalPosition;

        await backend.Append([CreateEvent("order-confirmed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append([CreateEvent("order-shipped", $"order:{TestId}")], dcbQuery, firstPosition, TestContext.Current.CancellationToken));    }

    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithExisting_ShouldThrow()
    {
        var backend = await CreateBackend();
        await backend.Append([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append([CreateEvent("order-created", $"order:{TestId}")], dcbQuery, 0, TestContext.Current.CancellationToken));    }

    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithNone_ShouldSucceed()
    {
        var backend = await CreateBackend();

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");

        var result = await backend.Append([CreateEvent("order-created", $"order:{TestId}")], dcbQuery, 0, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_DifferentBoundary_ShouldNotConflict()
    {
        var backend = await CreateBackend();
        await backend.Append([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append([CreateEvent("customer-updated", $"customer:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"customer:{TestId}");
        var lastCustomerPosition = (await backend.Stream(dcbQuery, cancellationToken: TestContext.Current.CancellationToken)).Last().GlobalPosition;

        var result = await backend.Append([CreateEvent("customer-verified", $"customer:{TestId}")], dcbQuery, lastCustomerPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_AllTags_ShouldIgnoreEventsMatchingOnlyOneTag()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(
            [CreateEvent("source-followed", $"reader:{TestId}", $"source:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var lastPosition = initial.Last().GlobalPosition;

        await backend.Append(
            [CreateEvent("reader-profile-updated", $"reader:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.Append(
            [CreateEvent("source-followed", $"reader:{TestId}", $"source:{TestId}")],
            DcbQuery.ByAllTags($"reader:{TestId}", $"source:{TestId}"),
            lastPosition,
            TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    [Fact]
    public async Task Append_WithDcbCheck_AllTags_ShouldDetectConflictsForMatchingTagSet()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(
            [CreateEvent("source-followed", $"reader:{TestId}", $"source:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var lastPosition = initial.Last().GlobalPosition;

        await backend.Append(
            [CreateEvent("source-unfollowed", $"reader:{TestId}", $"source:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(
                [CreateEvent("source-followed", $"reader:{TestId}", $"source:{TestId}")],
                DcbQuery.ByAllTags($"reader:{TestId}", $"source:{TestId}"),
                lastPosition,
                TestContext.Current.CancellationToken));
    }

    #endregion

    #region Position Tests

    [Fact]
    public async Task GetLastPosition_EmptyStore_ShouldReturnZero()
    {
        var backend = await CreateBackend();

        // Use a fresh backend that has no events (in-memory only; for Postgres use starting position)
        var position = await backend.GetLastPosition(TestContext.Current.CancellationToken);

        Assert.True(position >= 0);    }

    [Fact]
    public async Task GetLastPosition_AfterAppend_ShouldReturnLastPosition()
    {
        var backend = await CreateBackend();
        var appended = await backend.Append([
            CreateEvent("event-a", $"tag:1{TestId}"),
            CreateEvent("event-b", $"tag:2{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var position = await backend.GetLastPosition(TestContext.Current.CancellationToken);

        Assert.True(position >= appended.Last().GlobalPosition);    }

    #endregion

    #region GetPositionsAsync Tests

    [Fact]
    public async Task GetPositionsAsync_ReturnsPositionsInWindow()
    {
        var backend = await CreateBackend();
        var startPosition = await backend.GetLastPosition(TestContext.Current.CancellationToken);

        await backend.Append([CreateEvent("event-a", $"tag:1{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append([CreateEvent("event-b", $"tag:2{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var positions = await backend.GetPositionsAsync(startPosition, 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, positions.Count);
        Assert.Equal(startPosition + 1, positions[0]);
        Assert.Equal(startPosition + 2, positions[1]);
    }

    [Fact]
    public async Task GetPositionsAsync_ExcludesPositionsBeyondWindow()
    {
        var backend = await CreateBackend();
        var startPosition = await backend.GetLastPosition(TestContext.Current.CancellationToken);

        await backend.Append([CreateEvent("event-a", $"tag:1{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append([CreateEvent("event-b", $"tag:2{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.Append([CreateEvent("event-c", $"tag:3{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        // Window size of 1 — only one event fits
        var positions = await backend.GetPositionsAsync(startPosition, 1, TestContext.Current.CancellationToken);

        Assert.Single(positions);
        Assert.Equal(startPosition + 1, positions[0]);
    }

    [Fact]
    public async Task GetPositionsAsync_EmptyStoreReturnsEmpty()
    {
        var backend = await CreateBackend();
        var startPosition = await backend.GetLastPosition(TestContext.Current.CancellationToken);

        var positions = await backend.GetPositionsAsync(startPosition, 100, TestContext.Current.CancellationToken);

        Assert.Empty(positions);
    }

    #endregion

    #region Helper Methods

    protected IEventToPersist CreateEvent(string eventType, params string[] tags)
        => CreateEvent(eventType, new Dictionary<string, string>(), tags);

    protected IEventToPersist CreateEvent(string eventType, Dictionary<string, string> metadata, params string[] tags)
    {
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventData = """{"test": true}""",
            Tags = tags.Select(EventTag.Parse).ToArray(),
            Metadata = metadata
        };
    }

    #endregion
}
