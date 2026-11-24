using Alberto.InMemory;
using Xunit;

namespace Alberto.Tests;

/// <summary>
/// Guards the empty-query consistency-boundary defect fixed in V1.
///
/// An <see cref="DcbQuery"/> whose <see cref="DcbQuery.IsEmpty"/> is <c>true</c>
/// (no types and no tags) means "match everything" on the read path, but it cannot
/// define a conflict boundary for a consistency check. Before this fix the conflict
/// check was silently skipped, letting every concurrent writer win with no exception
/// and no log line. These tests verify that <see cref="EventStore.AppendAsync"/>
/// rejects any empty or null query when <c>expectedPosition</c> is supplied, and
/// that the normal paths — a non-empty query that detects a real conflict, and an
/// unconditional append with no expectedPosition — are unaffected.
/// </summary>
public class EmptyQueryBoundaryTests
{
    private static IEventToPersist AnyEvent() => new EventToPersist
    {
        EventType = new EventType("boundary-test-event"),
        Tags = [],
        EventData = "{}"
    };

    private static EventStore FreshStore() => new(new InMemoryEventStoreBackend());

    // ── Reject: empty ByTypes as a consistency check ─────────────────────────────────

    [Fact]
    public async Task AppendAsync_EmptyByTypes_WithExpectedPosition_Throws()
    {
        var emptyQuery = DcbQuery.ByTypes(Array.Empty<EventType>());
        Assert.True(emptyQuery.IsEmpty, "ByTypes with an empty array must be IsEmpty");

        var act = () => FreshStore().AppendAsync(
            [AnyEvent()],
            dcbQuery: emptyQuery,
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(act);
        Assert.Equal("dcbQuery", exception.ParamName);
    }

    // ── Reject: empty ByTags as a consistency check ──────────────────────────────────

    [Fact]
    public async Task AppendAsync_EmptyByTags_WithExpectedPosition_Throws()
    {
        var emptyQuery = DcbQuery.ByTags(Array.Empty<EventTag>());
        Assert.True(emptyQuery.IsEmpty, "ByTags with an empty array must be IsEmpty");

        var act = () => FreshStore().AppendAsync(
            [AnyEvent()],
            dcbQuery: emptyQuery,
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(act);
        Assert.Equal("dcbQuery", exception.ParamName);
    }

    // ── Reject: empty ByAllTags as a consistency check ───────────────────────────────

    [Fact]
    public async Task AppendAsync_EmptyByAllTags_WithExpectedPosition_Throws()
    {
        var emptyQuery = DcbQuery.ByAllTags(Array.Empty<EventTag>());
        Assert.True(emptyQuery.IsEmpty, "ByAllTags with an empty array must be IsEmpty");

        var act = () => FreshStore().AppendAsync(
            [AnyEvent()],
            dcbQuery: emptyQuery,
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(act);
        Assert.Equal("dcbQuery", exception.ParamName);
    }

    // ── Reject: null query with expectedPosition ──────────────────────────────────────

    [Fact]
    public async Task AppendAsync_NullQuery_WithExpectedPosition_Throws()
    {
        var act = () => FreshStore().AppendAsync(
            [AnyEvent()],
            dcbQuery: null,
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(act);
        Assert.Equal("dcbQuery", exception.ParamName);
    }

    // ── Allow: non-empty query with expectedPosition detects real conflict ────────────

    [Fact]
    public async Task AppendAsync_NonEmptyQuery_WithExpectedPosition_ConflictsCorrectly()
    {
        var store = FreshStore();
        var orderId = Guid.NewGuid().ToString("N");
        var query = DcbQuery.ByTags(new EventTag("order", orderId));

        // First append must succeed — there are no events in the boundary yet.
        await store.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("order-placed"),
                Tags = [new EventTag("order", orderId)],
                EventData = "{}"
            }],
            dcbQuery: query,
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        // A second append using the same stale expectedPosition=0 must be rejected
        // because the first append advanced the boundary.
        var act = () => store.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("order-cancelled"),
                Tags = [new EventTag("order", orderId)],
                EventData = "{}"
            }],
            dcbQuery: query,
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        // a non-empty query must still detect the conflict introduced by the first append
        await Assert.ThrowsAnyAsync<DcbConflictException>(act);
    }

    // ── Allow: unconditional append with an empty query (no expectedPosition) ─────────

    [Fact]
    public async Task AppendAsync_EmptyQuery_NoExpectedPosition_Succeeds()
    {
        var store = FreshStore();
        var emptyQuery = DcbQuery.ByTypes(Array.Empty<EventType>());

        // No expectedPosition → unconditional append; the empty query is irrelevant.
        var result = await store.AppendAsync(
            [AnyEvent()],
            dcbQuery: emptyQuery,
            expectedPosition: null,
            cancellationToken: TestContext.Current.CancellationToken);

        // the unconditional append must write the event
        Assert.Single(result);
    }

    // ── DcbQuery.All: intent is distinguishable from accidental emptiness ─────────────

    [Fact]
    public void All_IsStructurallyEmptyButFlaggedAsDeliberate()
    {
        Assert.True(DcbQuery.All.IsEmpty, "All names no types and no tags, so it is structurally identical to an accidental empty");
        Assert.True(DcbQuery.All.MatchesAll, "All is the one query whose emptiness was asked for by name");

        Assert.False(DcbQuery.Empty.MatchesAll, "Empty is the seed for the fluent builders, so an unnarrowed chain must read as a mistake");
        Assert.False(DcbQuery.ByTypes(Array.Empty<EventType>()).MatchesAll, "a filter list that came back empty at runtime was never a deliberate whole-log query");
    }

    [Fact]
    public void All_NarrowedByAnyAxis_IsAnOrdinaryQuery()
    {
        Assert.False(DcbQuery.All.WithTypes("order-placed").MatchesAll, "adding a type makes it a real boundary, not a whole-log query");
        Assert.False(DcbQuery.All.WithTags(new EventTag("order", "1")).MatchesAll, "adding a tag makes it a real boundary, not a whole-log query");
    }

    // ── Reject: DcbQuery.All as a consistency check, for a different reason ───────────

    [Fact]
    public async Task AppendAsync_All_WithExpectedPosition_ThrowsExplainingWhyItIsDeclined()
    {
        var act = () => FreshStore().AppendAsync(
            [AnyEvent()],
            dcbQuery: DcbQuery.All,
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        // All is a coherent request the store declines, so the message must explain the design
        // consequence rather than accuse the caller of a bug.
        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(act);
        Assert.Equal("dcbQuery", exception.ParamName);
        Assert.Contains("serialises every writer", exception.Message);
    }

    [Fact]
    public async Task AppendAsync_AccidentalEmpty_BlamesTheCallSiteNotTheDesign()
    {
        var act = () => FreshStore().AppendAsync(
            [AnyEvent()],
            dcbQuery: DcbQuery.ByTypes(Array.Empty<EventType>()),
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        // The two rejections must not collapse into one message: an empty filter list is a bug at
        // the call site, and pointing the caller at the whole-log design instead would send them
        // looking in the wrong place.
        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(act);
        var message = exception.Message;
        Assert.Contains("came back empty at runtime", message);
        Assert.DoesNotContain("serialises every writer", message);
    }

    // ── Allow: DcbQuery.All still reads the whole log ────────────────────────────────

    [Fact]
    public async Task StreamAsync_All_ReturnsEveryEvent()
    {
        var store = FreshStore();
        await store.AppendAsync([AnyEvent(), AnyEvent()],
            cancellationToken: TestContext.Current.CancellationToken);

        var events = await store.StreamAsync(
            DcbQuery.All,
            cancellationToken: TestContext.Current.CancellationToken).ToListAsync();

        // All remains a valid way to read the entire log
        Assert.Equal(2, events.Count());
    }
}
