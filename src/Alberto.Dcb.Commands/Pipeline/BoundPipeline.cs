namespace Alberto.Dcb;

/// <summary>
/// A pipeline whose state was read inside a DCB boundary. Because the boundary and the
/// position it was read at are both known, the decision that follows can be committed
/// with a conflict check and no further ceremony.
/// </summary>
public readonly struct BoundPipeline<TCommand, TState>
{
    private readonly AlbertoStore? _store;
    private readonly Func<CancellationToken, Task<Staged<TCommand>>>? _prepare;
    private readonly Func<TCommand, CancellationToken, Task<(TState State, DcbBoundary Boundary)>>? _load;

    internal BoundPipeline(
        AlbertoStore store,
        Func<CancellationToken, Task<Staged<TCommand>>> prepare,
        Func<TCommand, CancellationToken, Task<(TState State, DcbBoundary Boundary)>> load)
    {
        _store = store;
        _prepare = prepare;
        _load = load;
    }

    /// <summary>
    /// Applies the decision function to the command and the state read inside the boundary.
    /// </summary>
    /// <remarks>
    /// Synchronous by design. Anything awaited here would sit between the position
    /// observation and the append, widening the conflict window, and would be replayed
    /// by <c>RetryOnConflict</c>. Put IO in <c>Enrich</c> instead.
    /// </remarks>
    public BoundDecision Decide(Func<TCommand, TState, Decision> decide)
    {
        var (store, load, once) = Unwrap();
        return new BoundDecision(store, async ct =>
        {
            var staged = await once.Get(ct);
            if (staged.HasProblems)
                return (Decision.Fail(staged.Problems!), default);

            var (state, boundary) = await load(staged.Command, ct);
            return (decide(staged.Command, state), boundary);
        });
    }

    /// <inheritdoc cref="Decide(Func{TCommand, TState, Decision})"/>
    public BoundDecision Decide(Func<TState, Decision> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return Decide((_, state) => decide(state));
    }

    /// <inheritdoc cref="Decide(Func{TCommand, TState, Decision})"/>
    public BoundDecision<TValue> Decide<TValue>(Func<TCommand, TState, Decision<TValue>> decide)
    {
        var (store, load, once) = Unwrap();
        return new BoundDecision<TValue>(store, async ct =>
        {
            var staged = await once.Get(ct);
            if (staged.HasProblems)
                return (Decision<TValue>.Fail(staged.Problems!), default);

            var (state, boundary) = await load(staged.Command, ct);
            return (decide(staged.Command, state), boundary);
        });
    }

    /// <inheritdoc cref="Decide(Func{TCommand, TState, Decision})"/>
    public BoundDecision<TValue> Decide<TValue>(Func<TState, Decision<TValue>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return Decide((TCommand _, TState state) => decide(state));
    }

    private (
        AlbertoStore Store,
        Func<TCommand, CancellationToken, Task<(TState State, DcbBoundary Boundary)>> Load,
        Once<Staged<TCommand>> Prepared) Unwrap() =>
        (PipelineInternals.Require(_store), _load!, new Once<Staged<TCommand>>(_prepare!));
}
