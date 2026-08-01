using Alberto;
namespace Alberto.Commands;

/// <summary>
/// A pipeline whose state came from outside a DCB boundary. Nothing here knows what to
/// conflict-check against, so the terminal operations demand an explicit query and
/// position, or an explicit choice to skip the check.
/// </summary>
public readonly struct UnboundPipeline<TCommand, TState>
{
    private readonly AlbertoStore? _store;
    private readonly Func<CancellationToken, Task<Staged<TCommand>>>? _prepare;
    private readonly Func<TCommand, CancellationToken, Task<TState>>? _load;

    internal UnboundPipeline(
        AlbertoStore store,
        Func<CancellationToken, Task<Staged<TCommand>>> prepare,
        Func<TCommand, CancellationToken, Task<TState>> load)
    {
        _store = store;
        _prepare = prepare;
        _load = load;
    }

    /// <inheritdoc cref="BoundPipeline{TCommand, TState}.Decide(Func{TCommand, TState, Decision})"/>
    public UnboundDecision Decide(Func<TCommand, TState, Decision> decide)
    {
        var (store, load, once) = Unwrap();
        return new UnboundDecision(store, async ct =>
        {
            var staged = await once.Get(ct);
            if (staged.HasProblems)
                return Decision.Fail(staged.Problems!);

            var state = await load(staged.Command, ct);
            return decide(staged.Command, state);
        });
    }

    /// <inheritdoc cref="Decide(Func{TCommand, TState, Decision})"/>
    public UnboundDecision<TValue> Decide<TValue>(Func<TCommand, TState, Decision<TValue>> decide)
    {
        var (store, load, once) = Unwrap();
        return new UnboundDecision<TValue>(store, async ct =>
        {
            var staged = await once.Get(ct);
            if (staged.HasProblems)
                return Decision<TValue>.Fail(staged.Problems!);

            var state = await load(staged.Command, ct);
            return decide(staged.Command, state);
        });
    }

    private (
        AlbertoStore Store,
        Func<TCommand, CancellationToken, Task<TState>> Load,
        Once<Staged<TCommand>> Prepared) Unwrap() =>
        (PipelineInternals.Require(_store), _load!, new Once<Staged<TCommand>>(_prepare!));
}
