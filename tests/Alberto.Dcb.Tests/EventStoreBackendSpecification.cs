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

        var result = await backend.AppendAsync([eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await backend.AppendAsync(events, cancellationToken: TestContext.Current.CancellationToken);

        var positions = result.Select(e => e.GlobalPosition).ToList();
        Assert.Equal(3, positions.Count);
        Assert.True(positions[0] < positions[1] && positions[1] < positions[2]);    }

    [Fact]
    public async Task Append_ShouldPreserveEventData()
    {
        var backend = await CreateBackend();
        var eventToPersist = CreateEvent("order-placed", $"order:{TestId}");

        var result = await backend.AppendAsync([eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await backend.AppendAsync([eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result);    }

    [Fact]
    public async Task Stream_ByTags_ShouldReturnMatchingEvents()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent("order-placed", $"order:{TestId}"),
            CreateEvent("order-confirmed", $"order:{TestId}"),
            CreateEvent("customer-updated", $"customer:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAsync(DcbQuery.ByTags($"order:{TestId}"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Contains(e.Tags, t => t.Value == $"order:{TestId}"));    }

    [Fact]
    public async Task Stream_ByTypes_ShouldReturnMatchingEvents()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent($"order-placed-{TestId}", $"order:1{TestId}"),
            CreateEvent($"order-confirmed-{TestId}", $"order:1{TestId}"),
            CreateEvent($"order-placed-{TestId}", $"order:2{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAsync(DcbQuery.ByTypes($"order-placed-{TestId}"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal($"order-placed-{TestId}", e.EventType.Id));    }

    [Fact]
    public async Task Stream_ByTypesOrTags_AsUnion_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent($"order-placed-{TestId}", $"order:1{TestId}"),      // matches type
            CreateEvent($"order-confirmed-{TestId}", $"order:2{TestId}"),   // matches tag
            CreateEvent($"customer-updated-{TestId}", $"customer:3{TestId}") // matches neither
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes($"order-placed-{TestId}")
            .WithTags(new EventTag("order", $"2{TestId}"))
            .AsUnion();

        var result = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);    }

    [Fact]
    public async Task Stream_ByTypesAndTags_DefaultsToIntersect()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent($"order-placed-{TestId}", $"order:1{TestId}"),      // matches type only
            CreateEvent($"order-placed-{TestId}", $"order:2{TestId}"),      // matches both
            CreateEvent($"order-confirmed-{TestId}", $"order:2{TestId}"),   // matches tag only
            CreateEvent($"customer-updated-{TestId}", $"customer:3{TestId}") // matches neither
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes($"order-placed-{TestId}")
            .WithTags(new EventTag("order", $"2{TestId}"));

        var result = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        var matched = Assert.Single(result);
        Assert.Equal($"order-placed-{TestId}", matched.EventType.Id);
        Assert.Contains(matched.Tags, t => t.Value == $"order:2{TestId}");
    }

    [Fact]
    public async Task Stream_ByAllTags_ShouldRequireAllTagsToMatch()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent("source-followed", $"reader:{TestId}", $"source:{TestId}"),
            CreateEvent("source-unfollowed", $"reader:{TestId}"),
            CreateEvent("source-followed", $"source:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAsync(
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
        var startPosition = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        await backend.AppendAsync([
            CreateEvent("event-a", $"tag:1{TestId}"),
            CreateEvent("event-b", $"tag:2{TestId}"),
            CreateEvent("event-c", $"tag:3{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAllAsync(afterPosition: startPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);    }

    [Fact]
    public async Task Stream_WithAfterPosition_ShouldFilterByPosition()
    {
        var backend = await CreateBackend();
        var firstBatch = await backend.AppendAsync([
            CreateEvent("event-a", $"order:{TestId}"),
            CreateEvent("event-b", $"order:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([
            CreateEvent("event-c", $"order:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var afterPosition = firstBatch.Last().GlobalPosition;
        var result = await backend.StreamAsync(DcbQuery.ByTags($"order:{TestId}"), afterPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("event-c", result.First().EventType.Id);    }

    [Fact]
    public async Task Stream_WithLimit_ShouldLimitResults()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent("event-a", $"order:{TestId}"),
            CreateEvent("event-b", $"order:{TestId}"),
            CreateEvent("event-c", $"order:{TestId}"),
            CreateEvent("event-d", $"order:{TestId}"),
            CreateEvent("event-e", $"order:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAsync(DcbQuery.ByTags($"order:{TestId}"), limit: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);    }

    [Fact]
    public async Task Stream_ShouldOrderByPosition()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent("event-c", $"order:{TestId}"),
            CreateEvent("event-a", $"order:{TestId}"),
            CreateEvent("event-b", $"order:{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAsync(DcbQuery.ByTags($"order:{TestId}"), cancellationToken: TestContext.Current.CancellationToken);

        var positions = result.Select(e => e.GlobalPosition).ToList();
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p)));    }

    #endregion

    #region Boundary Composition Tests

    [Fact]
    public async Task Append_WithDcbCheck_TypesAndTags_DefaultsToIntersect_NoConflict()
    {
        var backend = await CreateBackend();
        var initial = await backend.AppendAsync(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        // Tag matches but type does not — under Intersect this is NOT a conflict.
        await backend.AppendAsync(
            [CreateEvent($"customer-updated-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}").WithTypes($"order-placed-{TestId}");
        var result = await backend.AppendAsync(
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
        var initial = await backend.AppendAsync(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        // Same type AND same tag — this should conflict under Intersect.
        await backend.AppendAsync(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}").WithTypes($"order-placed-{TestId}");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.AppendAsync(
                [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
                dcbQuery,
                lastPosition,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Append_WithDcbCheck_TypesAndTags_AsUnion_StillTreatsTagOnlyMatchAsConflict()
    {
        var backend = await CreateBackend();
        var initial = await backend.AppendAsync(
            [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        // Tag-only match — under Union semantics this conflicts with the boundary.
        await backend.AppendAsync(
            [CreateEvent($"customer-updated-{TestId}", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}").WithTypes($"order-placed-{TestId}").AsUnion();

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.AppendAsync(
                [CreateEvent($"order-placed-{TestId}", $"order:{TestId}")],
                dcbQuery,
                lastPosition,
                TestContext.Current.CancellationToken));
    }

    #endregion

    #region StreamAll Tests

    [Fact]
    public async Task StreamAll_ShouldReturnAllEvents()
    {
        var backend = await CreateBackend();

        // Get starting position to filter out events from other tests
        var startPosition = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        await backend.AppendAsync([CreateEvent("event-a", $"tag:1{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("event-b", $"tag:2{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.StreamAllAsync(afterPosition: startPosition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
    }

    #endregion

    #region DCB Consistency Tests

    [Fact]
    public async Task Append_WithDcbCheck_NoConflict_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var initial = await backend.AppendAsync([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");
        var result = await backend.AppendAsync([CreateEvent("order-confirmed", $"order:{TestId}")], dcbQuery, lastPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_WithConflict_ShouldThrow()
    {
        var backend = await CreateBackend();
        var initial = await backend.AppendAsync([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        var firstPosition = initial.First().GlobalPosition;

        await backend.AppendAsync([CreateEvent("order-confirmed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.AppendAsync([CreateEvent("order-shipped", $"order:{TestId}")], dcbQuery, firstPosition, TestContext.Current.CancellationToken));    }

    [Fact]
    public async Task Append_ConcurrentDcbChecks_SameBoundary_OnlyOneSucceeds()
    {
        // Regression guard for the DCB write-skew window: many writers race to
        // append the first event to the same boundary, all expecting it to be empty
        // (position 0). Exactly one must win; the rest must see the conflict. Without
        // append serialization, more than one could pass the check and commit.
        var backend = await CreateBackend();
        var tag = $"order:{TestId}-concurrency";
        var query = DcbQuery.ByTags(tag);
        const int writers = 12;

        // Release all writers together to maximise contention.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, writers).Select(async i =>
        {
            await gate.Task;
            try
            {
                await backend.AppendAsync([CreateEvent($"evt-{i}-{TestId}", tag)], query, 0L);
                return true;
            }
            catch (DcbConflictException)
            {
                return false;
            }
        }).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(succeeded => succeeded));

        var stored = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(stored);
    }

    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithExisting_ShouldThrow()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.AppendAsync([CreateEvent("order-created", $"order:{TestId}")], dcbQuery, 0, TestContext.Current.CancellationToken));    }

    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithNone_ShouldSucceed()
    {
        var backend = await CreateBackend();

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");

        var result = await backend.AppendAsync([CreateEvent("order-created", $"order:{TestId}")], dcbQuery, 0, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_DifferentBoundary_ShouldNotConflict()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("customer-updated", $"customer:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"customer:{TestId}");
        var lastCustomerPosition = (await backend.StreamAsync(dcbQuery, cancellationToken: TestContext.Current.CancellationToken)).Last().GlobalPosition;

        var result = await backend.AppendAsync([CreateEvent("customer-verified", $"customer:{TestId}")], dcbQuery, lastCustomerPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    [Fact]
    public async Task Append_WithDcbCheck_AllTags_ShouldIgnoreEventsMatchingOnlyOneTag()
    {
        var backend = await CreateBackend();
        var initial = await backend.AppendAsync(
            [CreateEvent("source-followed", $"reader:{TestId}", $"source:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var lastPosition = initial.Last().GlobalPosition;

        await backend.AppendAsync(
            [CreateEvent("reader-profile-updated", $"reader:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await backend.AppendAsync(
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
        var initial = await backend.AppendAsync(
            [CreateEvent("source-followed", $"reader:{TestId}", $"source:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var lastPosition = initial.Last().GlobalPosition;

        await backend.AppendAsync(
            [CreateEvent("source-unfollowed", $"reader:{TestId}", $"source:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.AppendAsync(
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
        var position = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        Assert.True(position >= 0);    }

    [Fact]
    public async Task GetLastPosition_AfterAppend_ShouldReturnLastPosition()
    {
        var backend = await CreateBackend();
        var appended = await backend.AppendAsync([
            CreateEvent("event-a", $"tag:1{TestId}"),
            CreateEvent("event-b", $"tag:2{TestId}")
        ], cancellationToken: TestContext.Current.CancellationToken);

        var position = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        Assert.True(position >= appended.Last().GlobalPosition);    }

    #endregion

    #region GetPositionsAsync Tests

    [Fact]
    public async Task GetPositionsAsync_ReturnsPositionsInWindow()
    {
        var backend = await CreateBackend();
        var headBackend = (IEventStoreHeadBackend)backend;
        var startPosition = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        await backend.AppendAsync([CreateEvent("event-a", $"tag:1{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("event-b", $"tag:2{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var positions = await headBackend.GetPositionsAsync(startPosition, 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, positions.Count);
        Assert.Equal(startPosition + 1, positions[0]);
        Assert.Equal(startPosition + 2, positions[1]);
    }

    [Fact]
    public async Task GetPositionsAsync_ExcludesPositionsBeyondWindow()
    {
        var backend = await CreateBackend();
        var headBackend = (IEventStoreHeadBackend)backend;
        var startPosition = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        await backend.AppendAsync([CreateEvent("event-a", $"tag:1{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("event-b", $"tag:2{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        await backend.AppendAsync([CreateEvent("event-c", $"tag:3{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        // Window size of 1 — only one event fits
        var positions = await headBackend.GetPositionsAsync(startPosition, 1, TestContext.Current.CancellationToken);

        Assert.Single(positions);
        Assert.Equal(startPosition + 1, positions[0]);
    }

    [Fact]
    public async Task GetPositionsAsync_EmptyStoreReturnsEmpty()
    {
        var backend = await CreateBackend();
        var headBackend = (IEventStoreHeadBackend)backend;
        var startPosition = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        var positions = await headBackend.GetPositionsAsync(startPosition, 100, TestContext.Current.CancellationToken);

        Assert.Empty(positions);
    }

    #endregion

    #region GetStableHeadAsync Tests

    [Fact]
    public async Task GetStableHeadAsync_AfterCommittedAppend_ExecutesBarrier()
    {
        // Regression guard for the PostgreSQL stable-head barrier. The SQL evaluates
        // pg_snapshot_xmin(pg_current_snapshot())::TEXT::BIGINT; the ::TEXT round-trip
        // is REQUIRED because PostgreSQL has no direct xid8→bigint cast. "Simplifying"
        // it to ::BIGINT throws "cannot cast type xid8 to bigint" at runtime — a defect
        // the pure-fake head tests cannot catch because they never touch the real SQL.
        // Appending a committed row first ensures the index scan has a row to evaluate.
        var backend = await CreateBackend();
        var headBackend = (IEventStoreHeadBackend)backend;
        var startPosition = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        await backend.AppendAsync(
            [CreateEvent("event-a", $"tag:1{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        // Every appended transaction has committed, so nothing is in flight: the barrier
        // must not throw and must not clamp below the pre-append head.
        var stableHead = await headBackend.GetStableHeadAsync(startPosition, TestContext.Current.CancellationToken);

        Assert.True(stableHead >= startPosition,
            $"expected stable head >= {startPosition}, got {stableHead}");
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
