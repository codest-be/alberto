namespace Alberto.Testing;

/// <summary>
/// The Then-verbs shared by <see cref="Specification{TState}"/> and
/// <see cref="StatelessSpecification"/>.
/// </summary>
/// <typeparam name="TSelf">
/// The concrete specification type. Every verb returns it rather than this base, so a chain never
/// narrows: <c>.ThenSucceeds().ThenState(...)</c> still sees the state-aware verbs.
/// </typeparam>
public abstract class SpecificationBase<TSelf> where TSelf : SpecificationBase<TSelf>
{
    private Decision? _decision;
    private object? _result;
    private bool _hasResult;

    private protected SpecificationBase() { }

    /// <summary>The derived instance, so every verb can return <typeparamref name="TSelf"/>.</summary>
    private protected abstract TSelf Self { get; }

    /// <summary>The decision produced by <c>When(...)</c>.</summary>
    private protected Decision Decision => _decision ?? throw new SpecificationException(
        "There is no decision to assert on yet. Call When(...) before any Then verb.");

    private protected void Act(Decision decision)
    {
        _decision = decision;
        _result = null;
        _hasResult = false;
    }

    private protected void Act<TResult>(Decision<TResult> decision)
    {
        _decision = decision;
        _hasResult = decision.IsSuccess;
        _result = decision.IsSuccess ? decision.Value : null;
    }

    /// <summary>Asserts the decision succeeded.</summary>
    public TSelf ThenSucceeds()
    {
        if (Decision.IsError)
            throw new SpecificationException(
                $"Expected the decision to succeed, but it failed with {Codes(Decision.Problems)}.{Detail(Decision.Problems)}");

        return Self;
    }

    /// <summary>Asserts the decision failed, without saying with what.</summary>
    public TSelf ThenFails()
    {
        if (Decision.IsSuccess)
            throw new SpecificationException(
                $"Expected the decision to fail, but it succeeded, emitting {Names(Decision.Events)}.");

        return Self;
    }

    /// <summary>
    /// Asserts the decision failed, carrying a problem with this code.
    /// </summary>
    public TSelf ThenFails(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        ThenFails();

        if (!Decision.Problems.Any(p => p.Code == code))
            throw new SpecificationException(
                $"Expected the decision to fail with problem code '{code}', " +
                $"but it failed with {Codes(Decision.Problems)}.{Detail(Decision.Problems)}");

        return Self;
    }

    /// <summary>
    /// Asserts the decision failed, carrying this problem.
    /// </summary>
    /// <remarks>
    /// Compared by <see cref="Problem.Code"/> only. The code is the machine-readable part and the
    /// only part callers branch on; the message is prose and the details are a dictionary, so
    /// requiring either to match would make the assertion brittle without making it stronger.
    /// Pass the factory that production calls — <c>ThenFails(OrderProblems.NotFound())</c> — and
    /// the code stays a single source of truth.
    /// </remarks>
    public TSelf ThenFails(Problem expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ThenFails(expected.Code);
    }

    /// <summary>
    /// Asserts the decision succeeded and emitted at least one <typeparamref name="TEvent"/>,
    /// optionally one matching <paramref name="match"/>.
    /// </summary>
    /// <remarks>
    /// Says nothing about the other events. Use <see cref="ThenEmitsOnly{TEvent}"/> when this is
    /// the only event the decision is allowed to emit.
    /// </remarks>
    public TSelf ThenEmits<TEvent>(Func<TEvent, bool>? match = null) where TEvent : IEvent
    {
        ThenSucceeds();

        var candidates = Decision.Events.OfType<TEvent>().ToList();

        if (candidates.Count == 0)
            throw new SpecificationException(
                $"Expected the decision to emit a {typeof(TEvent).Name}, " +
                $"but it emitted {Names(Decision.Events)}.{Detail(Decision.Events)}");

        if (match is not null && !candidates.Any(match))
            throw new SpecificationException(
                $"The decision emitted {candidates.Count} {typeof(TEvent).Name} event(s), " +
                $"but none matched the predicate.{Detail(candidates)}");

        return Self;
    }

    /// <summary>
    /// Asserts the decision succeeded and emitted exactly one event, of type
    /// <typeparamref name="TEvent"/>, optionally matching <paramref name="match"/>.
    /// </summary>
    public TSelf ThenEmitsOnly<TEvent>(Func<TEvent, bool>? match = null) where TEvent : IEvent
    {
        ThenSucceeds();

        if (Decision.Events.Count != 1)
            throw new SpecificationException(
                $"Expected the decision to emit exactly one {typeof(TEvent).Name}, " +
                $"but it emitted {Names(Decision.Events)}.{Detail(Decision.Events)}");

        return ThenEmits(match);
    }

    /// <summary>
    /// Asserts the decision succeeded without emitting anything — the idempotent no-op, where
    /// the command was already satisfied and there is nothing left to record.
    /// </summary>
    public TSelf ThenEmitsNothing()
    {
        ThenSucceeds();

        if (Decision.Events.Count != 0)
            throw new SpecificationException(
                $"Expected the decision to emit nothing, but it emitted {Names(Decision.Events)}.{Detail(Decision.Events)}");

        return Self;
    }

    /// <summary>
    /// The escape hatch: hands you every emitted event, in order, for assertions the verbs above
    /// do not cover.
    /// </summary>
    public TSelf ThenEvents(Action<IReadOnlyList<IEvent>> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(Decision.Events);
        return Self;
    }

    /// <summary>
    /// The escape hatch for failures: hands you every problem, in order. Use it when the code is
    /// not the whole of what you mean to pin — the message a status guard composes, say, or the
    /// details a caller is expected to branch on.
    /// </summary>
    public TSelf ThenProblems(Action<IReadOnlyList<Problem>> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(Decision.Problems);
        return Self;
    }

    /// <summary>
    /// Asserts on the value a <see cref="Decision{T}"/> carried. Requires the
    /// <c>When</c> overload that returns one.
    /// </summary>
    public TSelf ThenResult<TResult>(Action<TResult> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        ThenSucceeds();

        if (!_hasResult)
            throw new SpecificationException(
                "The decision carries no result value. Use the When(...) overload that returns " +
                "Decision<TResult> when you want to assert on one.");

        if (_result is not TResult typed)
            throw new SpecificationException(
                $"Expected a result of type {typeof(TResult).Name}, but the decision carried " +
                $"{(_result is null ? "null" : _result.GetType().Name)}.");

        assert(typed);
        return Self;
    }

    private static string Codes(IReadOnlyList<Problem> problems) =>
        problems.Count == 0 ? "no problems" : string.Join(", ", problems.Select(p => $"'{p.Code}'"));

    private static string Names(IReadOnlyList<IEvent> events) =>
        events.Count == 0 ? "nothing" : string.Join(", ", events.Select(e => e.GetType().Name));

    private static string Detail<T>(IReadOnlyList<T> items) =>
        items.Count == 0
            ? string.Empty
            : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", items);
}
