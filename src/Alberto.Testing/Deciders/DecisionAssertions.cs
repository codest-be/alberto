namespace Alberto.Testing;

/// <summary>
/// The Then-verbs that read a decision. Every one of them needs a decision to read, so this type
/// is only ever reached through <c>When(...)</c> — there is no stage of the chain that exposes a
/// decision verb before one has been made.
/// </summary>
/// <typeparam name="TSelf">
/// The concrete stage. Every verb returns it rather than this base, so a chain never narrows:
/// <c>.ThenSucceeds().ThenState(...)</c> still sees the verbs the stage adds on top of these.
/// </typeparam>
/// <remarks>
/// The four stages that derive from it — with state or without, carrying a result or not — differ
/// only in what they add. What a decision *is* does not change between them, so the verbs that
/// read one live here and are written once.
/// </remarks>
public abstract class DecisionAssertions<TSelf> where TSelf : DecisionAssertions<TSelf>
{
    private protected DecisionAssertions(Decision decision) => Decision = decision;

    /// <summary>The decision produced by <c>When(...)</c>.</summary>
    private protected Decision Decision { get; }

    /// <summary>The derived instance, so every verb can return <typeparamref name="TSelf"/>.</summary>
    private protected abstract TSelf Self { get; }

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

    private static string Codes(IReadOnlyList<Problem> problems) =>
        problems.Count == 0 ? "no problems" : string.Join(", ", problems.Select(p => $"'{p.Code}'"));

    private static string Names(IReadOnlyList<IEvent> events) =>
        events.Count == 0 ? "nothing" : string.Join(", ", events.Select(e => e.GetType().Name));

    private static string Detail<T>(IReadOnlyList<T> items) =>
        items.Count == 0
            ? string.Empty
            : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", items);
}
