using System.Text.Json.Nodes;
using Alberto;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Testing.Xunit;

/// <summary>
/// Specification tests for <see cref="IEventStoreBackend"/> implementations.
/// These tests define the contract that all implementations must follow.
///
/// Derive from this class and implement <see cref="CreateBackend"/> to run Alberto's own
/// event-store conformance suite against your backend.
/// </summary>
public abstract class EventStoreBackendSpecification
{
    /// <summary>
    /// Unique tag prefix generated per test instance for isolation (avoids cross-test interference).
    /// </summary>
    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Controllable clock fixed to 2025-01-15T10:30:00Z. The backend under test is passed
    /// this provider where it accepts a <c>TimeProvider</c> parameter.
    /// </summary>
    protected FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero));

    /// <summary>
    /// Factory method called once per fact to create the backend under test.
    /// </summary>
    protected abstract Task<IEventStoreBackend> CreateBackend();

    // ── Capability hooks ─────────────────────────────────────────────────────

    /// <summary>
    /// True when the backend under test supports <see cref="IEventStoreBackend.StreamAllAsync"/>.
    ///
    /// <para>
    /// The default is <see langword="true"/>. Set this to <see langword="false"/> for backends
    /// that intentionally restrict cross-tenant streaming — specifically, the request-scoped
    /// tenant decorator (<c>InMemoryTenantEventStoreDecorator</c> /
    /// <c>TenantEventStoreDecorator</c>) whose <c>StreamAllAsync</c> throws
    /// <see cref="InvalidOperationException"/> when <c>HasTenant</c> is true. That restriction
    /// is a deliberate isolation guard, not a missing feature; setting this hook to
    /// <see langword="false"/> skips the two <c>StreamAllAsync</c> facts rather than treating
    /// the guard as a contract violation.
    /// </para>
    /// </summary>
    protected virtual bool SupportsStreamAll => true;

    #region Append Tests

    /// <summary>A single event must be persisted and returned by <c>AppendAsync</c>.</summary>
    [Fact]
    public async Task Append_SingleEvent_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var eventToPersist = CreateEvent("order-placed", $"order:{TestId}");

        var result = await backend.AppendAsync([eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(eventToPersist.Id, result.First().Id);
        Assert.Equal(eventToPersist.EventType.Id, result.First().EventType.Id);    }

    /// <summary>Multiple events in one batch must receive strictly increasing global positions.</summary>
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

    /// <summary>The event payload and tags stored must match those supplied to <c>AppendAsync</c>.</summary>
    /// <remarks>
    /// Semantically, not byte for byte. A backend is free to normalize the payload — PostgreSQL's
    /// <c>jsonb</c> column does, and the in-memory backend does too — so this asserts what
    /// <see cref="IEventEnvelope.EventData"/> actually promises. The payload here is deliberately
    /// non-canonical (padded, keys out of order, nested) so that a backend which round-trips
    /// through a JSON representation is genuinely exercised rather than handed something that
    /// happens to already be in its own canonical form.
    /// </remarks>
    [Fact]
    public async Task Append_ShouldPreserveEventData()
    {
        var backend = await CreateBackend();
        const string payload =
            """{  "orderId" : "abc" ,  "amount": 100,  "lines":[ {"sku":"x","qty":2} ] , "z": null }""";
        var eventToPersist = CreateEventWithData("order-placed", payload, $"order:{TestId}");

        var result = await backend.AppendAsync([eventToPersist], cancellationToken: TestContext.Current.CancellationToken);

        var appended = result.First();
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(appended.EventData)),
            $"EventData must round-trip as semantically equal JSON. Sent {payload}, got {appended.EventData}.");
        Assert.Equal(eventToPersist.Tags.Count, appended.Tags.Count);    }

    /// <summary>
    /// A payload that is not well-formed JSON must be refused, rather than stored and left to fail
    /// on the way out. PostgreSQL's <c>jsonb</c> column enforces this; a backend that does not is a
    /// test double which accepts data the real store would reject.
    /// </summary>
    [Fact]
    public async Task Append_MalformedEventData_ShouldThrow()
    {
        var backend = await CreateBackend();
        var eventToPersist = CreateEventWithData("order-placed", """{"orderId": }""", $"order:{TestId}");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            backend.AppendAsync([eventToPersist], cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A NUL in the payload must be refused. The escape below is well-formed JSON that most
    /// parsers accept, but PostgreSQL has no representation for U+0000 in a text value and rejects
    /// it at the <c>jsonb</c> cast — so a backend that accepts it is one where the append
    /// succeeds in tests and fails in production.
    /// </summary>
    [Fact]
    public async Task Append_EventDataContainingNul_ShouldThrow()
    {
        var backend = await CreateBackend();
        // Written as an escape in the JSON text, not as a NUL in the C# string — the point
        // is the six characters a serializer would legitimately emit for U+0000.
        var eventToPersist = CreateEventWithData(
            "order-placed", "{\"orderId\": \"a\\u0000b\"}", $"order:{TestId}");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            backend.AppendAsync([eventToPersist], cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>All metadata key–value pairs supplied to <c>AppendAsync</c> must survive round-trip.</summary>
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

    /// <summary>Streaming a tag that has never been written to must return an empty collection.</summary>
    [Fact]
    public async Task Stream_EmptyStore_ShouldReturnEmpty()
    {
        var backend = await CreateBackend();
        var query = DcbQuery.ByTags($"order:{TestId}-empty");

        var result = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result);    }

    /// <summary>A tag query must return only events whose tag set contains that tag value.</summary>
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

    /// <summary>An event-type query must return only events whose type matches the specified value.</summary>
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

    /// <summary>A union query must return events matching either the type axis or the tag axis.</summary>
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

    /// <summary>An intersect (default) query must return only events matching both the type and tag axes.</summary>
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

    /// <summary>
    /// An intersect query whose tag axis lists several tags carried by the same event must
    /// return that event once.  The Postgres backend serves this with a tag-driven semi-join
    /// (migration 028) that emits one row per matching tag before deduplication, so this is
    /// the shape that fails if the deduplication is dropped.
    /// </summary>
    [Fact]
    public async Task Stream_ByTypesAndTags_EventCarryingSeveralRequestedTags_ShouldReturnItOnce()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent($"order-placed-{TestId}", $"order:1{TestId}", $"customer:1{TestId}"),    // both requested tags
            CreateEvent($"order-placed-{TestId}", $"order:9{TestId}"),                            // requested type, unrequested tag
            CreateEvent($"order-confirmed-{TestId}", $"order:1{TestId}", $"customer:1{TestId}")   // both tags, unrequested type
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes($"order-placed-{TestId}")
            .WithTags($"order:1{TestId}", $"customer:1{TestId}");

        var result = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        var matched = Assert.Single(result);
        Assert.Equal($"order-placed-{TestId}", matched.EventType.Id);
    }

    /// <summary>
    /// A duplicate must not consume a slot of the limit.  With the deduplication dropped, an
    /// event carrying two of the requested tags fills the limit by itself and hides the events
    /// behind it — a wrong result rather than merely a repeated one.
    /// </summary>
    [Fact]
    public async Task Stream_ByTypesAndTags_WithLimit_ShouldNotSpendSlotsOnDuplicates()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([
            CreateEvent($"order-placed-{TestId}", $"order:1{TestId}", $"customer:1{TestId}"),    // matches two of the requested tags
            CreateEvent($"order-placed-{TestId}", $"order:2{TestId}")                             // matches one
        ], cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.Empty
            .WithTypes($"order-placed-{TestId}")
            .WithTags($"order:1{TestId}", $"customer:1{TestId}", $"order:2{TestId}");

        var result = await backend.StreamAsync(query, limit: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result.Select(e => e.GlobalPosition).Distinct().Count());
        Assert.Contains(result, e => e.Tags.Any(t => t.Value == $"order:2{TestId}"));
    }

    /// <summary>A <c>ByAllTags</c> query must return only events whose tag set contains every listed tag.</summary>
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

    /// <summary>An empty query (no type or tag filter) must return all events after the given position.</summary>
    [Fact]
    public async Task Stream_EmptyQuery_ShouldReturnAllEvents()
    {
        if (!SupportsStreamAll)
            Assert.Skip(
                "This backend does not support StreamAllAsync when a tenant is in scope. " +
                "The tenant decorator intentionally throws to prevent cross-tenant data leakage; " +
                "this restriction is a deliberate isolation guard, not a missing feature.");

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

    /// <summary>When an <c>afterPosition</c> is supplied only events with a greater position must be returned.</summary>
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

    /// <summary>When a <c>limit</c> is supplied the result must contain at most that many events.</summary>
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

    /// <summary>Events must be returned in ascending global-position order regardless of insertion order.</summary>
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

    /// <summary>
    /// A DCB check using type+tag intersect must not see an event that matches only the tag axis
    /// as a conflict — the event does not satisfy the boundary.
    /// </summary>
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

    /// <summary>
    /// A DCB check using type+tag intersect must detect a conflict when an event satisfies
    /// both axes simultaneously.
    /// </summary>
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

    /// <summary>
    /// A union query used as a DCB boundary treats a tag-only match as a conflict, because
    /// the boundary includes all events matching either axis.
    /// </summary>
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

    /// <summary><c>StreamAllAsync</c> must return every event appended after the given position.</summary>
    [Fact]
    public async Task StreamAll_ShouldReturnAllEvents()
    {
        if (!SupportsStreamAll)
            Assert.Skip(
                "This backend does not support StreamAllAsync when a tenant is in scope. " +
                "The tenant decorator intentionally throws to prevent cross-tenant data leakage; " +
                "this restriction is a deliberate isolation guard, not a missing feature.");

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

    /// <summary>
    /// An append with a DCB boundary check must succeed when no events matching the boundary
    /// have been written after the caller's known position.
    /// </summary>
    [Fact]
    public async Task Append_WithDcbCheck_NoConflict_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var initial = await backend.AppendAsync([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);
        var lastPosition = initial.Last().GlobalPosition;

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");
        var result = await backend.AppendAsync([CreateEvent("order-confirmed", $"order:{TestId}")], dcbQuery, lastPosition, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    /// <summary>
    /// An append with a DCB boundary check must throw <see cref="DcbConflictException"/> when a
    /// newer event satisfies the boundary after the caller's known position.
    /// </summary>
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

    /// <summary>
    /// When multiple writers race to be first to append to an empty boundary (position 0),
    /// exactly one must succeed and the rest must receive <see cref="DcbConflictException"/>.
    /// </summary>
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

    /// <summary>
    /// A DCB check expecting an empty boundary (position 0) must throw <see cref="DcbConflictException"/>
    /// when matching events already exist.
    /// </summary>
    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithExisting_ShouldThrow()
    {
        var backend = await CreateBackend();
        await backend.AppendAsync([CreateEvent("order-placed", $"order:{TestId}")], cancellationToken: TestContext.Current.CancellationToken);

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.AppendAsync([CreateEvent("order-created", $"order:{TestId}")], dcbQuery, 0, TestContext.Current.CancellationToken));    }

    /// <summary>
    /// A DCB check expecting an empty boundary (position 0) must succeed when no matching events exist.
    /// </summary>
    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithNone_ShouldSucceed()
    {
        var backend = await CreateBackend();

        var dcbQuery = DcbQuery.ByTags($"order:{TestId}");

        var result = await backend.AppendAsync([CreateEvent("order-created", $"order:{TestId}")], dcbQuery, 0, TestContext.Current.CancellationToken);

        Assert.Single(result);    }

    /// <summary>
    /// A boundary scoped to one tag must not be affected by events tagged with a different tag.
    /// </summary>
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

    /// <summary>
    /// An all-tags boundary must ignore events that match only a subset of the required tags.
    /// </summary>
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

    /// <summary>
    /// An all-tags boundary must detect a conflict when a new event carries the full required tag set.
    /// </summary>
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

    /// <summary>
    /// <c>GetLastPositionAsync</c> must return a non-negative value even when the store is empty.
    /// </summary>
    [Fact]
    public async Task GetLastPosition_EmptyStore_ShouldReturnZero()
    {
        var backend = await CreateBackend();

        // Use a fresh backend that has no events (in-memory only; for Postgres use starting position)
        var position = await backend.GetLastPositionAsync(TestContext.Current.CancellationToken);

        Assert.True(position >= 0);    }

    /// <summary>
    /// After an append <c>GetLastPositionAsync</c> must return a value at least as large as
    /// the position of the last appended event.
    /// </summary>
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

    /// <summary>
    /// <c>GetPositionsAsync</c> must return the positions of events committed after the given
    /// start position, up to the specified window size.
    /// </summary>
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

    /// <summary>
    /// <c>GetPositionsAsync</c> must exclude positions beyond the window boundary even when
    /// more events exist.
    /// </summary>
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

    /// <summary>
    /// <c>GetPositionsAsync</c> must return an empty list when no events have been appended
    /// after the given position.
    /// </summary>
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

    /// <summary>
    /// <c>GetStableHeadAsync</c> must execute without error after a committed append and
    /// must not clamp the result below the pre-append head.
    ///
    /// This is a regression guard for the PostgreSQL stable-head barrier SQL: the
    /// <c>::TEXT</c> round-trip cast is required because PostgreSQL has no direct
    /// <c>xid8→bigint</c> cast. The pure-fake head tests cannot catch a missing cast
    /// because they never touch the real SQL path.
    /// </summary>
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

    #region Version Tag Regression Tests

    // These two tests verify the safety guarantees that the schema versioning design rests on.
    // They run against BOTH the InMemory and Postgres backends because the two implementations
    // are maintained independently — the InMemory matcher and the Postgres HAVING-COUNT
    // containment check are separately implemented and have diverged on subtle points before.

    [Fact]
    public async Task ByAllTags_WithExtraVersionTag_ContainmentNotEquality()
    {
        // After schema versioning, every appended event carries an extra reserved _version:N tag.
        // A ByAllTags boundary that specifies only user-domain tags must still match those
        // events. This requires the underlying query to use CONTAINMENT (event tags ⊇ query
        // tags), not set equality.  If the implementation were equality-based, this test
        // would return zero results after the _v tag was added, silently breaking every
        // existing boundary in production.
        var backend = await CreateBackend();

        var userTag = $"order:{TestId}-containment";

        // Append an event that carries BOTH the user tag and the reserved version tag. The
        // version tag is the only thing in this suite that reaches for an Alberto internal:
        // EventTag's public surface refuses reserved concepts by design, and a backend is handed
        // events that already carry the tag because EventSerializer stamps it one layer up.
        await backend.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("versioned-thing", 2),
                EventData = """{"test":true}""",
                Tags = [new EventTag("order", $"{TestId}-containment"), EventTag.ForVersion(2)],
                Metadata = new Dictionary<string, string>()
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        // Query specifies ONLY the user-domain tag — no version tag in the boundary.
        var results = await backend.StreamAsync(
            DcbQuery.ByAllTags(userTag),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);

        // The other half of the same contract: the stored tag is where the version comes from on
        // read. A backend that persists the tag but does not project it back into EventType would
        // pass the containment assertion above and still lose every event's schema version.
        Assert.Equal(2, results.Single().EventType.Version);
    }

    [Fact]
    public async Task LegacyEvent_WithoutVersionTag_ReadsAsVersion1()
    {
        // Events written before schema versioning was introduced have no _v tag in storage.
        // On read, both backends must return Version = 1 for such events.
        // This is the entire back-compat story for every row already in production.
        var backend = await CreateBackend();

        var tag = $"order:{TestId}-legacy";

        await backend.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("legacy-event-type"),  // version defaults to 1
                EventData = """{"test":true}""",
                Tags = [new EventTag("order", $"{TestId}-legacy")],  // no _version tag
                Metadata = new Dictionary<string, string>()
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        var results = await backend.StreamAsync(
            DcbQuery.ByTags(tag),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(1, results.Single().EventType.Version);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates an <see cref="IEventToPersist"/> with the given type and tags and an empty metadata dictionary.
    /// </summary>
    /// <param name="eventType">The event type identifier.</param>
    /// <param name="tags">One or more tag values in <c>concept:value</c> format.</param>
    protected IEventToPersist CreateEvent(string eventType, params string[] tags)
        => CreateEvent(eventType, new Dictionary<string, string>(), tags);

    /// <summary>
    /// Creates an <see cref="IEventToPersist"/> with the given type, metadata, and tags.
    /// </summary>
    /// <param name="eventType">The event type identifier.</param>
    /// <param name="metadata">Metadata key–value pairs to attach to the event.</param>
    /// <param name="tags">One or more tag values in <c>concept:value</c> format.</param>
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

    /// <summary>
    /// Creates an <see cref="IEventToPersist"/> carrying a specific payload.
    /// </summary>
    /// <remarks>
    /// Distinctly named rather than another <c>CreateEvent</c> overload: <c>CreateEvent(type, x)</c>
    /// already means "one tag", and an overload taking the payload in that position would silently
    /// change what every existing two-argument call site does.
    /// </remarks>
    /// <param name="eventType">The event type identifier.</param>
    /// <param name="eventData">The JSON payload, verbatim.</param>
    /// <param name="tags">One or more tag values in <c>concept:value</c> format.</param>
    protected IEventToPersist CreateEventWithData(string eventType, string eventData, params string[] tags)
    {
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventData = eventData,
            Tags = tags.Select(EventTag.Parse).ToArray(),
            Metadata = new Dictionary<string, string>()
        };
    }

    #endregion
}
