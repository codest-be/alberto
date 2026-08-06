using Alberto;
namespace Alberto.Commands;

/// <summary>
/// A decision made inside a DCB boundary, ready to commit. <c>Commit(ct)</c> is available
/// here and nowhere else: reaching this type is the proof that a boundary was established.
/// </summary>
public readonly struct BoundDecision
{
    private readonly AlbertoStore? _store;
    private readonly Func<CancellationToken, Task<(Decision Decision, DcbBoundary Boundary)>>? _attempt;
    private readonly int _attempts;

    internal BoundDecision(
        AlbertoStore store,
        Func<CancellationToken, Task<(Decision Decision, DcbBoundary Boundary)>> attempt)
        : this(store, attempt, 1)
    {
    }

    private BoundDecision(
        AlbertoStore store,
        Func<CancellationToken, Task<(Decision Decision, DcbBoundary Boundary)>> attempt,
        int attempts)
    {
        _store = store;
        _attempt = attempt;
        _attempts = attempts;
    }

    /// <summary>
    /// Re-reads the boundary and re-decides on conflict, up to <paramref name="attempts"/>
    /// times in total. Enrichment is not repeated.
    /// </summary>
    /// <param name="attempts">Total attempts including the first. Must be at least 1.</param>
    public BoundDecision RetryOnConflict(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        return new BoundDecision(PipelineInternals.Require(_store), _attempt!, attempts);
    }

    /// <summary>
    /// Appends the decided events under the boundary, checked against the position the
    /// state was read at. Throws <see cref="DcbConflictException"/> if the boundary moved.
    /// </summary>
    public async Task<Result> Commit(CancellationToken cancellationToken)
    {
        var store = PipelineInternals.Require(_store);
        var attempt = _attempt!;
        return await ConflictRetryLoop.Run(
            _attempts,
            attempt,
            async (bd, ct) => await store.Persist(
                bd.Decision, bd.Boundary.Query, bd.Boundary.ExpectedPosition, ct),
            cancellationToken);
    }

    /// <summary>
    /// As <see cref="Commit"/>, but a conflict comes back as a <c>dcb.conflict</c> problem
    /// rather than an exception.
    /// </summary>
    public async Task<Result> TryCommit(CancellationToken cancellationToken)
    {
        try
        {
            return await Commit(cancellationToken);
        }
        catch (DcbConflictException exception)
        {
            return Result.Fail(PipelineInternals.Conflict(exception));
        }
    }
}

/// <inheritdoc cref="BoundDecision"/>
public readonly struct BoundDecision<TValue>
{
    private readonly AlbertoStore? _store;
    private readonly Func<CancellationToken, Task<(Decision<TValue> Decision, DcbBoundary Boundary)>>? _attempt;
    private readonly int _attempts;

    internal BoundDecision(
        AlbertoStore store,
        Func<CancellationToken, Task<(Decision<TValue> Decision, DcbBoundary Boundary)>> attempt)
        : this(store, attempt, 1)
    {
    }

    private BoundDecision(
        AlbertoStore store,
        Func<CancellationToken, Task<(Decision<TValue> Decision, DcbBoundary Boundary)>> attempt,
        int attempts)
    {
        _store = store;
        _attempt = attempt;
        _attempts = attempts;
    }

    /// <summary>Transforms the value the commit will return. Events are untouched.</summary>
    public BoundDecision<TNew> Map<TNew>(Func<TValue, TNew> map)
    {
        var store = PipelineInternals.Require(_store);
        var attempt = _attempt!;
        var attempts = _attempts;
        return new BoundDecision<TNew>(
            store,
            async ct =>
            {
                var (decision, boundary) = await attempt(ct);
                return (decision.Map(map), boundary);
            },
            attempts);
    }

    /// <inheritdoc cref="BoundDecision.RetryOnConflict"/>
    public BoundDecision<TValue> RetryOnConflict(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        return new BoundDecision<TValue>(PipelineInternals.Require(_store), _attempt!, attempts);
    }

    /// <inheritdoc cref="BoundDecision.Commit"/>
    public async Task<Result<TValue>> Commit(CancellationToken cancellationToken)
    {
        var store = PipelineInternals.Require(_store);
        var attempt = _attempt!;
        return await ConflictRetryLoop.Run(
            _attempts,
            attempt,
            async (bd, ct) => await store.Persist(
                bd.Decision, bd.Boundary.Query, bd.Boundary.ExpectedPosition, ct),
            cancellationToken);
    }

    /// <inheritdoc cref="BoundDecision.TryCommit"/>
    public async Task<Result<TValue>> TryCommit(CancellationToken cancellationToken)
    {
        try
        {
            return await Commit(cancellationToken);
        }
        catch (DcbConflictException exception)
        {
            return Result<TValue>.Fail(PipelineInternals.Conflict(exception));
        }
    }
}

/// <summary>
/// The retry loop shared by <see cref="BoundDecision.Commit"/> and
/// <see cref="BoundDecision{TValue}.Commit"/>. The retry semantics live here once.
/// </summary>
internal static class ConflictRetryLoop
{
    /// <summary>
    /// Runs a prepare-then-commit loop, retrying up to <paramref name="attempts"/> times
    /// total on <see cref="DcbConflictException"/> from the commit step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <paramref name="attempts"/> argument is always &gt;= 1 at the call site: the
    /// internal constructor passes 1, and <c>RetryOnConflict</c> validates the argument
    /// before storing it. <c>Math.Max</c> is therefore not needed and is deliberately absent.
    /// </para>
    /// <para>
    /// <paramref name="prepare"/> runs <em>outside</em> the catch filter: a
    /// <see cref="DcbConflictException"/> thrown during the prepare step propagates to the
    /// caller without being retried. Only <paramref name="commit"/> is inside the catch.
    /// This mirrors the original inline loop's try scope: the loop retries the persist,
    /// and only the persist. <paramref name="prepare"/> is a separate delegate — not the
    /// first statement of <paramref name="commit"/> — precisely to enforce that split.
    /// </para>
    /// <para>
    /// On conflict the loop re-invokes <paramref name="prepare"/>, which re-reads the
    /// boundary and re-decides — but does not repeat enrichment, because enrichment lives
    /// behind a <c>Once{T}</c> that memoises the first call.
    /// </para>
    /// </remarks>
    internal static async Task<TResult> Run<TState, TResult>(
        int attempts,
        Func<CancellationToken, Task<TState>> prepare,
        Func<TState, CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken)
    {
        var remaining = attempts;
        while (true)
        {
            var state = await prepare(cancellationToken);
            try
            {
                return await commit(state, cancellationToken);
            }
            catch (DcbConflictException) when (--remaining > 0)
            {
                // Re-read the boundary and decide again against what is there now.
            }
        }
    }
}
