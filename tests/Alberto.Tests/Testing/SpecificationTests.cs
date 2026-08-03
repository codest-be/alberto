using Alberto.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class SpecificationTests
{
    // ── A miniature slice, in the shape a real one has ─────────────────────────────

    [EventType("tab-opened")]
    private sealed record TabOpened(string TabId) : IEvent;

    [EventType("round-ordered")]
    private sealed record RoundOrdered(decimal Amount) : IEvent;

    [EventType("tab-closed")]
    private sealed record TabClosed : IEvent;

    [EventType("kitchen-notified")]
    private sealed record KitchenNotified : IEvent;

    /// <summary>An event no evolver below declares a handler for.</summary>
    [EventType("lights-dimmed")]
    private sealed record LightsDimmed : IEvent;

    private sealed record TabState
    {
        public string TabId { get; init; } = "";
        public decimal Total { get; init; }
        public bool IsClosed { get; init; }
        public bool Exists => TabId.Length > 0;
    }

    private sealed class TabEvolver : Evolver<TabState>,
        IEvolve<TabState, TabOpened>,
        IEvolve<TabState, RoundOrdered>,
        IEvolve<TabState, TabClosed>
    {
        public TabState Apply(TabState s, TabOpened e) => s with { TabId = e.TabId };
        public TabState Apply(TabState s, RoundOrdered e) => s with { Total = s.Total + e.Amount };
        public TabState Apply(TabState s, TabClosed e) => s with { IsClosed = true };
    }

    private static readonly Problem NotFound = Problem.Create("tab.not-found", "No such tab.");

    private static Decision Order(TabState state, decimal amount) =>
        !state.Exists ? Decision.Fail(NotFound)
        : state.IsClosed ? Decision.Fail("tab.closed", "The tab is closed.")
        : amount == 0 ? Decision.Succeed()
        : Decision.Succeed(new RoundOrdered(amount), new KitchenNotified());

    // ── Given ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Given_folds_the_events_in_order_before_the_decider_runs()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"), new RoundOrdered(10m), new RoundOrdered(5m))
            .When(state => Order(state, 2m))
            .ThenState(s => Assert.Equal(17m, s.Total)); // 15 given + 2 emitted
    }

    [Fact]
    public void Given_ignores_events_the_evolver_declares_no_handler_for()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"), new LightsDimmed())
            .When(state => Order(state, 1m))
            .ThenSucceeds();
    }

    [Fact]
    public void Given_accumulates_across_calls_so_arrange_helpers_can_hand_one_back()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"))
            .Given(new RoundOrdered(4m))
            .When(state => Order(state, 0m))
            .ThenState(s => Assert.Equal(4m, s.Total));
    }

    [Fact]
    public void Given_can_start_from_a_hand_built_state()
    {
        Spec.For(new TabEvolver())
            .Given(new TabState { TabId = "t-1", Total = 100m }, new RoundOrdered(1m))
            .When(state => Order(state, 0m))
            .ThenState(s => Assert.Equal(101m, s.Total));
    }

    [Fact]
    public void GivenNoEvents_leaves_the_state_at_its_default()
    {
        Spec.For(new TabEvolver())
            .GivenNoEvents()
            .When(state => Order(state, 1m))
            .ThenFails(NotFound);
    }

    [Fact]
    public void Given_after_When_is_rejected_rather_than_silently_ignored()
    {
        var spec = Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"))
            .When(state => Order(state, 1m));

        var ex = Assert.Throws<SpecificationException>(() => spec.Given(new RoundOrdered(1m)));
        Assert.Contains("before When", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Then_verb_before_When_is_rejected()
    {
        var spec = Spec.For(new TabEvolver()).Given(new TabOpened("t-1"));

        var ex = Assert.Throws<SpecificationException>(() => spec.ThenSucceeds());
        Assert.Contains("Call When(...)", ex.Message, StringComparison.Ordinal);
    }

    // ── ThenSucceeds / ThenFails ──────────────────────────────────────────────────

    [Fact]
    public void ThenSucceeds_reports_the_problem_codes_when_the_decision_failed()
    {
        var ex = Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .GivenNoEvents()
                .When(state => Order(state, 1m))
                .ThenSucceeds());

        Assert.Contains("'tab.not-found'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThenFails_reports_what_was_emitted_when_the_decision_succeeded()
    {
        var ex = Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .Given(new TabOpened("t-1"))
                .When(state => Order(state, 1m))
                .ThenFails());

        Assert.Contains("RoundOrdered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThenFails_by_code_and_by_problem_agree()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"), new TabClosed())
            .When(state => Order(state, 1m))
            .ThenFails("tab.closed")
            .ThenFails();
    }

    [Fact]
    public void ThenFails_matches_a_problem_on_its_code_not_its_message()
    {
        Spec.For(new TabEvolver())
            .GivenNoEvents()
            .When(state => Order(state, 1m))
            .ThenFails(Problem.Create("tab.not-found", "an entirely different message"));
    }

    [Fact]
    public void ThenFails_with_the_wrong_code_names_the_code_that_actually_came_back()
    {
        var ex = Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .GivenNoEvents()
                .When(state => Order(state, 1m))
                .ThenFails("tab.closed"));

        Assert.Contains("'tab.closed'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'tab.not-found'", ex.Message, StringComparison.Ordinal);
    }

    // ── ThenEmits / ThenEmitsOnly / ThenEmitsNothing ──────────────────────────────

    [Fact]
    public void ThenEmits_finds_its_event_among_the_others()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"))
            .When(state => Order(state, 7m))
            .ThenEmits<RoundOrdered>(e => e.Amount == 7m)
            .ThenEmits<KitchenNotified>();
    }

    [Fact]
    public void ThenEmitsOnly_refuses_a_decision_that_emitted_more_than_the_one_event()
    {
        var ex = Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .Given(new TabOpened("t-1"))
                .When(state => Order(state, 7m))
                .ThenEmitsOnly<RoundOrdered>());

        Assert.Contains("exactly one RoundOrdered", ex.Message, StringComparison.Ordinal);
        Assert.Contains("KitchenNotified", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThenEmits_says_the_predicate_missed_rather_than_that_the_event_is_absent()
    {
        var ex = Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .Given(new TabOpened("t-1"))
                .When(state => Order(state, 7m))
                .ThenEmits<RoundOrdered>(e => e.Amount == 8m));

        Assert.Contains("none matched the predicate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Amount = 7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThenEmits_on_a_failed_decision_reports_the_failure_not_a_missing_event()
    {
        var ex = Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .GivenNoEvents()
                .When(state => Order(state, 1m))
                .ThenEmits<RoundOrdered>());

        Assert.Contains("Expected the decision to succeed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThenEmitsNothing_accepts_the_idempotent_no_op()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"))
            .When(state => Order(state, 0m))
            .ThenEmitsNothing();
    }

    [Fact]
    public void ThenEmitsNothing_rejects_a_decision_that_emitted()
    {
        Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .Given(new TabOpened("t-1"))
                .When(state => Order(state, 1m))
                .ThenEmitsNothing());
    }

    [Fact]
    public void ThenEvents_hands_over_every_event_in_order()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"))
            .When(state => Order(state, 3m))
            .ThenEvents(events =>
            {
                Assert.Equal(2, events.Count);
                Assert.IsType<RoundOrdered>(events[0]);
                Assert.IsType<KitchenNotified>(events[1]);
            });
    }

    // ── ThenState ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ThenState_sees_the_emitted_events_folded_in()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"), new RoundOrdered(10m))
            .When(state => Order(state, 5m))
            .ThenState(s =>
            {
                Assert.Equal(15m, s.Total);
                Assert.False(s.IsClosed);
            });
    }

    [Fact]
    public void ThenState_after_a_failure_says_so_rather_than_asserting_on_stale_state()
    {
        var ex = Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .GivenNoEvents()
                .When(state => Order(state, 1m))
                .ThenState(_ => { }));

        Assert.Contains("ThenFails", ex.Message, StringComparison.Ordinal);
    }

    // ── The chain does not narrow ─────────────────────────────────────────────────

    [Fact]
    public void Every_verb_returns_the_full_specification_so_a_chain_never_narrows()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"))
            .When(state => Order(state, 5m))
            .ThenSucceeds()
            .ThenEmits<RoundOrdered>()
            .ThenEvents(events => Assert.Equal(2, events.Count))
            .ThenState(s => Assert.Equal(5m, s.Total)); // still reachable after four Then verbs
    }

    // ── Decision<T> ───────────────────────────────────────────────────────────────

    private static Decision<string> OrderReturningReference(TabState state, decimal amount) =>
        !state.Exists
            ? Decision<string>.Fail(NotFound)
            : Decision<string>.Succeed($"ref-{state.TabId}", new RoundOrdered(amount));

    [Fact]
    public void ThenResult_asserts_on_the_value_a_typed_decision_carried()
    {
        Spec.For(new TabEvolver())
            .Given(new TabOpened("t-1"))
            .When(state => OrderReturningReference(state, 5m))
            .ThenEmitsOnly<RoundOrdered>()
            .ThenResult<string>(r => Assert.Equal("ref-t-1", r))
            .ThenState(s => Assert.Equal(5m, s.Total));
    }

    [Fact]
    public void ThenResult_on_an_untyped_decision_points_at_the_right_When_overload()
    {
        var ex = Assert.Throws<SpecificationException>(() =>
            Spec.For(new TabEvolver())
                .Given(new TabOpened("t-1"))
                .When(state => Order(state, 5m))
                .ThenResult<string>(_ => { }));

        Assert.Contains("Decision<TResult>", ex.Message, StringComparison.Ordinal);
    }

    // ── Stateless ─────────────────────────────────────────────────────────────────

    private static Decision Open(string tabId) =>
        string.IsNullOrWhiteSpace(tabId)
            ? Decision.Fail("tab.id-required", "A tab id is required.")
            : Decision.Succeed(new TabOpened(tabId));

    [Fact]
    public void A_stateless_decider_needs_no_evolver_and_no_history()
    {
        Spec.Stateless()
            .When(() => Open("t-1"))
            .ThenEmitsOnly<TabOpened>(e => e.TabId == "t-1");

        Spec.Stateless()
            .When(() => Open(""))
            .ThenFails("tab.id-required");
    }

    [Fact]
    public void A_stateless_decider_can_carry_a_result_too()
    {
        Spec.Stateless()
            .When(() => Decision<int>.Succeed(42, new TabOpened("t-1")))
            .ThenEmitsOnly<TabOpened>()
            .ThenResult<int>(r => Assert.Equal(42, r));
    }
}
