namespace Alberto.Dcb;

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
        var remaining = Math.Max(_attempts, 1);

        while (true)
        {
            var (decision, boundary) = await attempt(cancellationToken);
            try
            {
                return await store.Persist(
                    decision, boundary.Query, boundary.ExpectedPosition, cancellationToken);
            }
            catch (DcbConflictException) when (--remaining > 0)
            {
                // Re-read the boundary and decide again against what is there now.
            }
        }
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
        var remaining = Math.Max(_attempts, 1);

        while (true)
        {
            var (decision, boundary) = await attempt(cancellationToken);
            try
            {
                return await store.Persist(
                    decision, boundary.Query, boundary.ExpectedPosition, cancellationToken);
            }
            catch (DcbConflictException) when (--remaining > 0)
            {
                // Re-read the boundary and decide again against what is there now.
            }
        }
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
