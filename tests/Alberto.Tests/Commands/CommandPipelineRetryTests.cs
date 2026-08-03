using Alberto;
using Alberto.Commands;
using Alberto.InMemory;
using System.Text.Json;
using Xunit;

namespace Alberto.Tests.Commands;

/// <summary>
/// Covers NoCoverage gaps in Alberto.Commands pipeline types.
///
/// Targeted mutants (all NoCoverage — no existing test reaches the code):
///   BoundDecision.cs L146   — --remaining > 0 retry condition (++ and >= variants)
///   BoundDecision.cs L117   — BoundDecision{TValue}.Map body
///   BoundDecision.cs L127   — BoundDecision{TValue}.RetryOnConflict(0) guard
///   UnboundDecision.cs L32  — UnboundDecision.TryCommit try body
///   UnboundDecision.cs L36  — UnboundDecision.TryCommit catch body
///   UnboundDecision.cs L67  — UnboundDecision{TValue}.Map body
///   UnboundDecision.cs L87  — UnboundDecision{TValue}.TryCommit try body
///   UnboundDecision.cs L91  — UnboundDecision{TValue}.TryCommit catch body
///   UnboundPipeline.cs L32  — if (staged.HasProblems) short-circuit in Decide
///   CommandPipeline.cs L70  — both ternary branches in Map{TNew}
///   CommandPipeline.cs L218 — ArgumentNullException guard on LoadUnder async overload
///   BoundPipeline.cs L50    — ArgumentNullException guard on Decide(Func{TState,Decision})
///   BoundPipeline.cs L72    — ArgumentNullException guard on Decide{TValue}(Func{TState,Decision{TValue}})
/// </summary>
public sealed class CommandPipelineRetryTests
{
    // -----------------------------------------------------------------------
    // Shared domain types — slugs are unique to this file so there is no
    // collision with other test classes that may scan the assembly.
    // -----------------------------------------------------------------------

    [EventType("rtest-widget-created")]
    private sealed record WidgetCreated : IEvent;

    private sealed record CreateWidget(string Name);

    private sealed record WidgetState
    {
        public static readonly WidgetState Empty = new();
    }

    // -----------------------------------------------------------------------
    // ConflictingEventStore: throws DcbConflictException on the first N calls
    // to AppendAsync, then returns an empty envelope list. All other members
    // return empty/zero so that Load (StreamAsync) works without real data.
    //
    // AppendAttempts lets each test assert the exact number of persist calls
    // the pipeline made, which is the observable that kills the three L146
    // mutants: ++remaining (unbounded), --remaining >= 0 (one extra retry),
    // and --remaining < 0 (no retries at all).
    // -----------------------------------------------------------------------

    private sealed class ConflictingEventStore(int conflictsBeforeSuccess) : IEventStore
    {
        private int _appendAttempts;
        public int AppendAttempts => _appendAttempts;

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events,
            DcbQuery? dcbQuery = null,
            long? expectedPosition = null,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _appendAttempts);
            if (attempt <= conflictsBeforeSuccess)
                throw new DcbConflictException(
                    conflictingPosition: attempt,
                    expectedPosition: expectedPosition ?? 0,
                    query: dcbQuery ?? DcbQuery.Empty);

            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query,
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }

    // Builds an AlbertoStore wired to the provided IEventStore. The serializer
    // knows only about WidgetCreated; no other event type is needed here.
    private static AlbertoStore MakeStore(IEventStore eventStore)
    {
        var registry = new Dictionary<string, Type>
        {
            [EventTypeAttribute.GetEventTypeId(typeof(WidgetCreated))] = typeof(WidgetCreated)
        };
        var serializer = EventSerializer.FromRegistry(
            registry, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return new AlbertoStore(eventStore, serializer);
    }

    // Builds an AlbertoStore backed by a fresh, empty in-memory event store.
    private static AlbertoStore InMemoryStore()
    {
        var backend = new InMemoryEventStoreBackend();
        return MakeStore(new EventStore(backend));
    }

    // -----------------------------------------------------------------------
    // BoundDecision retry loop — exact attempt-count assertions.
    //
    // These tests use a store that throws DcbConflictException on the first N
    // AppendAsync calls. Asserting the exact call count kills all three L146
    // mutants:
    //   --remaining >= 0  → RetryOnConflict(1) would allow one extra retry
    //                        (attempt would be 2, not 1)
    //   --remaining < 0   → RetryOnConflict(2) with one conflict would never
    //                        retry (attempt would be 1, not 2)
    //   ++remaining       → remaining grows instead of shrinks; the first
    //                        test would spin forever rather than throw
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RetryOnConflict_1Attempt_ThrowsWithoutRetrying()
    {
        // The store always conflicts; with one total attempt no retry is allowed.
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: int.MaxValue);
        var store = MakeStore(fake);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            store.Handle(new CreateWidget("w"))
                .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
                .Decide((_, _) => Decision.Succeed(new WidgetCreated()))
                .RetryOnConflict(1)
                .Commit(TestContext.Current.CancellationToken));

        // Exactly 1 persist attempt — no retry loop was entered.
        Assert.Equal(1, fake.AppendAttempts);
    }

    [Fact]
    public async Task RetryOnConflict_2Attempts_SucceedsAfterOneConflict()
    {
        // Store conflicts once, then succeeds on the second call.
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: 1);
        var store = MakeStore(fake);

        var result = await store.Handle(new CreateWidget("w"))
            .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
            .Decide((_, _) => Decision.Succeed(new WidgetCreated()))
            .RetryOnConflict(2)
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        // Two persist calls: one conflict, one success.
        Assert.Equal(2, fake.AppendAttempts);
    }

    [Fact]
    public async Task RetryOnConflict_3Attempts_SucceedsAfterTwoConflicts()
    {
        // Store conflicts twice, then succeeds on the third call.
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: 2);
        var store = MakeStore(fake);

        var result = await store.Handle(new CreateWidget("w"))
            .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
            .Decide((_, _) => Decision.Succeed(new WidgetCreated()))
            .RetryOnConflict(3)
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        // Three persist calls: two conflicts, one success.
        Assert.Equal(3, fake.AppendAttempts);
    }

    [Fact]
    public async Task RetryOnConflict_ExhaustingAllAttempts_RethrowsDcbConflictException()
    {
        // Always conflicts — pipeline exhausts RetryOnConflict(2) and gives up.
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: int.MaxValue);
        var store = MakeStore(fake);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            store.Handle(new CreateWidget("w"))
                .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
                .Decide((_, _) => Decision.Succeed(new WidgetCreated()))
                .RetryOnConflict(2)
                .Commit(TestContext.Current.CancellationToken));

        // Two attempts were made before giving up.
        Assert.Equal(2, fake.AppendAttempts);
    }

    // -----------------------------------------------------------------------
    // BoundDecision<TValue> retry loop (L146) — exact attempt-count assertions.
    //
    // The generic BoundDecision<TValue>.Commit has an identical retry loop to
    // BoundDecision.Commit but at a different line (L146 vs L60). Stryker
    // generates independent mutants for each copy, so the non-generic tests
    // above do NOT kill the L146 mutants. These tests use Decision<int> so the
    // pipeline returns BoundDecision<int> and the generic loop is exercised.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RetryOnConflict_ValuedDecision_1Attempt_ThrowsWithoutRetrying()
    {
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: int.MaxValue);
        var store = MakeStore(fake);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            store.Handle(new CreateWidget("w"))
                .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
                .Decide((_, _) => Decision<int>.Succeed(1, new WidgetCreated()))
                .RetryOnConflict(1)
                .Commit(TestContext.Current.CancellationToken));

        Assert.Equal(1, fake.AppendAttempts);
    }

    [Fact]
    public async Task RetryOnConflict_ValuedDecision_2Attempts_SucceedsAfterOneConflict()
    {
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: 1);
        var store = MakeStore(fake);

        var result = await store.Handle(new CreateWidget("w"))
            .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
            .Decide((_, _) => Decision<int>.Succeed(42, new WidgetCreated()))
            .RetryOnConflict(2)
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(2, fake.AppendAttempts);
    }

    [Fact]
    public async Task RetryOnConflict_ValuedDecision_3Attempts_SucceedsAfterTwoConflicts()
    {
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: 2);
        var store = MakeStore(fake);

        var result = await store.Handle(new CreateWidget("w"))
            .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
            .Decide((_, _) => Decision<int>.Succeed(99, new WidgetCreated()))
            .RetryOnConflict(3)
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(99, result.Value);
        Assert.Equal(3, fake.AppendAttempts);
    }

    [Fact]
    public async Task RetryOnConflict_ValuedDecision_ExhaustingAllAttempts_Rethrows()
    {
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: int.MaxValue);
        var store = MakeStore(fake);

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            store.Handle(new CreateWidget("w"))
                .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
                .Decide((_, _) => Decision<int>.Succeed(0, new WidgetCreated()))
                .RetryOnConflict(2)
                .Commit(TestContext.Current.CancellationToken));

        Assert.Equal(2, fake.AppendAttempts);
    }

    // -----------------------------------------------------------------------
    // BoundDecision<TValue>.RetryOnConflict(0) — kills L127.
    // The non-generic overload is covered in CommandPipelineTests; the generic
    // one lives at a different line and needs its own test.
    // -----------------------------------------------------------------------

    [Fact]
    public void RetryOnConflict_ValuedDecision_ZeroAttempts_ThrowsArgumentOutOfRangeException()
    {
        var store = InMemoryStore();

        var decision = store.Handle(new CreateWidget("w"))
            .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s)
            .Decide((_, _) => Decision<int>.Succeed(42, new WidgetCreated()));

        Assert.Throws<ArgumentOutOfRangeException>(() => decision.RetryOnConflict(0));
    }

    // -----------------------------------------------------------------------
    // BoundDecision<TValue>.Map — kills L117-121.
    // Map wraps the attempt closure to transform the committed value; events
    // are carried through unchanged.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BoundDecision_Map_TransformsCommittedValueAndPreservesEvents()
    {
        var store = InMemoryStore();

        // A real tag-based query is required: DcbQuery.Empty is rejected by the
        // in-memory store when an expectedPosition is supplied. The GUID makes
        // the boundary fresh (no existing events) so there is no conflict.
        var boundary = DcbQuery.ByTags(new EventTag("widget-id", Guid.NewGuid().ToString()));

        var result = await store.Handle(new CreateWidget("hello"))
            .Load(boundary, WidgetState.Empty, (s, _) => s)
            .Decide((cmd, _) => Decision<string>.Succeed(cmd.Name, new WidgetCreated()))
            .Map(name => name.Length)  // BoundDecision<string> → BoundDecision<int>
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello".Length, result.Value);
    }

    // -----------------------------------------------------------------------
    // UnboundDecision.TryCommit — kills L32 (try body) and L36 (catch body).
    // These are both NoCoverage because TryCommit(query, pos, ct) is never
    // called anywhere in the existing suite.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnboundDecision_TryCommit_SuccessPath_ReturnsOkResult()
    {
        // Covers the try body (L32): TryCommit delegates to Commit successfully.
        // A real tag-based query is required: DcbQuery.Empty is rejected when
        // expectedPosition is non-null. The GUID ensures no prior events exist.
        var store = InMemoryStore();
        var boundary = DcbQuery.ByTags(new EventTag("widget-id", Guid.NewGuid().ToString()));

        var result = await store.Handle(new CreateWidget("w"))
            .Decide(_ => Decision.Succeed(new WidgetCreated()))
            .TryCommit(boundary, expectedPosition: 0, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task UnboundDecision_TryCommit_ConflictPath_ReturnsConflictProblemInsteadOfThrowing()
    {
        // Covers the catch body (L36): the store throws DcbConflictException which
        // TryCommit converts to a dcb.conflict Problem rather than propagating.
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: 1);
        var store = MakeStore(fake);

        var result = await store.Handle(new CreateWidget("w"))
            .Decide(_ => Decision.Succeed(new WidgetCreated()))
            .TryCommit(DcbQuery.Empty, expectedPosition: 0, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(DcbConflictException.ProblemCode, Assert.Single(result.Problems).Code);
    }

    // -----------------------------------------------------------------------
    // UnboundDecision<TValue>.Map — kills L67.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnboundDecision_Generic_Map_TransformsValueBeforeCommit()
    {
        // Covers Map body (L67): produces a new UnboundDecision<int> that applies
        // the transform when the underlying decide closure runs.
        var store = InMemoryStore();

        var result = await store.Handle(new CreateWidget("hi"))
            .Decide(cmd => Decision<string>.Succeed(cmd.Name, new WidgetCreated()))
            .Map(s => s.Length)  // UnboundDecision<string> → UnboundDecision<int>
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("hi".Length, result.Value);
    }

    // -----------------------------------------------------------------------
    // UnboundDecision<TValue>.TryCommit — kills L87 (try body) and L91 (catch body).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnboundDecision_Generic_TryCommit_SuccessPath_ReturnsValue()
    {
        // Covers the try body (L87).
        // A real tag-based query is required: DcbQuery.Empty is rejected when
        // expectedPosition is non-null. The GUID ensures no prior events exist.
        var store = InMemoryStore();
        var boundary = DcbQuery.ByTags(new EventTag("widget-id", Guid.NewGuid().ToString()));

        var result = await store.Handle(new CreateWidget("w"))
            .Decide(cmd => Decision<string>.Succeed(cmd.Name, new WidgetCreated()))
            .TryCommit(boundary, expectedPosition: 0, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("w", result.Value);
    }

    [Fact]
    public async Task UnboundDecision_Generic_TryCommit_ConflictPath_ReturnsConflictProblem()
    {
        // Covers the catch body (L91): conflict becomes a Problem, not an exception.
        var fake = new ConflictingEventStore(conflictsBeforeSuccess: 1);
        var store = MakeStore(fake);

        var result = await store.Handle(new CreateWidget("w"))
            .Decide(cmd => Decision<string>.Succeed(cmd.Name, new WidgetCreated()))
            .TryCommit(DcbQuery.Empty, expectedPosition: 0, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(DcbConflictException.ProblemCode, Assert.Single(result.Problems).Code);
    }

    // -----------------------------------------------------------------------
    // UnboundDecision<TValue>.CommitUnconditionally — success path.
    // Not explicitly listed in the target mutants but also NoCoverage.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnboundDecision_Generic_CommitUnconditionally_ReturnsValueWithNoConflictCheck()
    {
        var store = InMemoryStore();

        var result = await store.Handle(new CreateWidget("widget-42"))
            .Decide(cmd => Decision<int>.Succeed(cmd.Name.Length, new WidgetCreated()))
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("widget-42".Length, result.Value);
    }

    // -----------------------------------------------------------------------
    // UnboundPipeline.Decide — kills L32 (the if (staged.HasProblems) block).
    //
    // The Decide closure on UnboundPipeline checks staged.HasProblems before
    // calling the user's decider. To reach that branch, validation must fail
    // before LoadUnbound produces the pipeline. Both the non-generic and the
    // generic overload have the same guard; both are tested.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnboundPipeline_Decide_WhenValidationFailed_ShortCircuitsWithoutCallingDecider()
    {
        var store = InMemoryStore();
        var deciderCalled = false;

        var result = await store.Handle(new CreateWidget("w"))
            .Validate(_ => Problem.Create("widget.bad", "No good"))
            .LoadUnbound(_ => Task.FromResult(WidgetState.Empty))
            .Decide((_, _) =>
            {
                deciderCalled = true;
                return Decision.Succeed(new WidgetCreated());
            })
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("widget.bad", Assert.Single(result.Problems).Code);
        Assert.False(deciderCalled, "Decider must not run when validation has already failed.");
    }

    [Fact]
    public async Task UnboundPipeline_DecideGeneric_WhenValidationFailed_ShortCircuits()
    {
        // Covers the generic Decide overload's identical HasProblems guard.
        var store = InMemoryStore();
        var deciderCalled = false;

        var result = await store.Handle(new CreateWidget("w"))
            .Validate(_ => Problem.Create("widget.bad", "No good"))
            .LoadUnbound(_ => Task.FromResult(WidgetState.Empty))
            .Decide((_, _) =>
            {
                deciderCalled = true;
                return Decision<int>.Succeed(0, new WidgetCreated());
            })
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("widget.bad", Assert.Single(result.Problems).Code);
        Assert.False(deciderCalled, "Decider must not run when validation has already failed.");
    }

    // -----------------------------------------------------------------------
    // CommandPipeline.Map<TNew> — kills both L70 ternary mutants.
    //
    // Map checks staged.HasProblems to decide whether to invoke the transform.
    // One mutant flips the condition, one replaces the failed branch. Covering
    // both the success path (transform runs) and the failure path (transform
    // must not run, problems pass through) kills both.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CommandPipeline_Map_WhenPipelineIsHealthy_TransformsCommandType()
    {
        var store = InMemoryStore();

        // Map converts CreateWidget → int (name length), then Decide works on int.
        var result = await store.Handle(new CreateWidget("hello"))
            .Map(cmd => cmd.Name.Length)
            .Decide(n => Decision.Succeed(new WidgetCreated()))
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CommandPipeline_Map_WhenValidationFailed_PropagatesFailureWithoutCallingTransform()
    {
        var store = InMemoryStore();
        var transformCalled = false;

        // Validation failure is established before Map runs. The map function
        // must not be called; the failure must be the validation problem.
        var result = await store.Handle(new CreateWidget("w"))
            .Validate(_ => Problem.Create("widget.invalid", "Bad input"))
            .Map(cmd =>
            {
                transformCalled = true;
                return cmd.Name.Length;
            })
            .Decide(n => Decision.Succeed(new WidgetCreated()))
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("widget.invalid", Assert.Single(result.Problems).Code);
        Assert.False(transformCalled, "Map transform must not execute when problems are already set.");
    }

    // -----------------------------------------------------------------------
    // ArgumentNullException guards — each covers one ThrowIfNull call that
    // Stryker reports as NoCoverage because the overload is never exercised.
    // -----------------------------------------------------------------------

    [Fact]
    public void CommandPipeline_LoadUnder_AsyncTaskLoader_NullArg_ThrowsArgumentNullException()
    {
        // CommandPipeline.cs L218: LoadUnder(Func<TCommand, Task<(TState, DcbQuery, long)>>)
        var store = InMemoryStore();
        var pipeline = store.Handle(new CreateWidget("w"));

        Assert.Throws<ArgumentNullException>(() =>
            pipeline.LoadUnder(
                (Func<CreateWidget, Task<(WidgetState State, DcbQuery Boundary, long Position)>>)null!));
    }

    [Fact]
    public void BoundPipeline_DecideStateOnly_NullDecider_ThrowsArgumentNullException()
    {
        // BoundPipeline.cs L50: Decide(Func<TState, Decision>)
        var store = InMemoryStore();
        var boundPipeline = store.Handle(new CreateWidget("w"))
            .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s);

        Assert.Throws<ArgumentNullException>(() =>
            boundPipeline.Decide((Func<WidgetState, Decision>)null!));
    }

    [Fact]
    public void BoundPipeline_DecideValuedStateOnly_NullDecider_ThrowsArgumentNullException()
    {
        // BoundPipeline.cs L72: Decide<TValue>(Func<TState, Decision<TValue>>)
        var store = InMemoryStore();
        var boundPipeline = store.Handle(new CreateWidget("w"))
            .Load(DcbQuery.Empty, WidgetState.Empty, (s, _) => s);

        Assert.Throws<ArgumentNullException>(() =>
            boundPipeline.Decide((Func<WidgetState, Decision<int>>)null!));
    }
}
