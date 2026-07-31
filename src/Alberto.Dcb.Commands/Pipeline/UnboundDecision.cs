using Alberto.Dcb;
namespace Alberto.Commands;

/// <summary>
/// A decision made without a DCB boundary. There is no <c>Commit(ct)</c>: the caller
/// either supplies the query and position it observed, or states outright that no
/// conflict check is wanted.
/// </summary>
public readonly struct UnboundDecision
{
    private readonly AlbertoStore? _store;
    private readonly Func<CancellationToken, Task<Decision>>? _decide;

    internal UnboundDecision(AlbertoStore store, Func<CancellationToken, Task<Decision>> decide)
    {
        _store = store;
        _decide = decide;
    }

    /// <summary>Appends against a query and position the caller observed itself.</summary>
    public async Task<Result> Commit(DcbQuery query, long expectedPosition, CancellationToken cancellationToken)
    {
        var store = PipelineInternals.Require(_store);
        var decision = await _decide!(cancellationToken);
        return await store.Persist(decision, query, expectedPosition, cancellationToken);
    }

    /// <inheritdoc cref="BoundDecision.TryCommit"/>
    public async Task<Result> TryCommit(DcbQuery query, long expectedPosition, CancellationToken cancellationToken)
    {
        try
        {
            return await Commit(query, expectedPosition, cancellationToken);
        }
        catch (DcbConflictException exception)
        {
            return Result.Fail(PipelineInternals.Conflict(exception));
        }
    }

    /// <summary>
    /// Appends with no conflict check at all. Named explicitly so an unobserved query
    /// can never masquerade as a consistency boundary.
    /// </summary>
    public async Task<Result> CommitUnconditionally(CancellationToken cancellationToken)
    {
        var store = PipelineInternals.Require(_store);
        var decision = await _decide!(cancellationToken);
        return await store.Persist(decision, DcbQuery.Empty, null, cancellationToken);
    }
}

/// <inheritdoc cref="UnboundDecision"/>
public readonly struct UnboundDecision<TValue>
{
    private readonly AlbertoStore? _store;
    private readonly Func<CancellationToken, Task<Decision<TValue>>>? _decide;

    internal UnboundDecision(AlbertoStore store, Func<CancellationToken, Task<Decision<TValue>>> decide)
    {
        _store = store;
        _decide = decide;
    }

    /// <inheritdoc cref="BoundDecision{TValue}.Map{TNew}"/>
    public UnboundDecision<TNew> Map<TNew>(Func<TValue, TNew> map)
    {
        var store = PipelineInternals.Require(_store);
        var decide = _decide!;
        return new UnboundDecision<TNew>(store, async ct => (await decide(ct)).Map(map));
    }

    /// <inheritdoc cref="UnboundDecision.Commit"/>
    public async Task<Result<TValue>> Commit(
        DcbQuery query, long expectedPosition, CancellationToken cancellationToken)
    {
        var store = PipelineInternals.Require(_store);
        var decision = await _decide!(cancellationToken);
        return await store.Persist(decision, query, expectedPosition, cancellationToken);
    }

    /// <inheritdoc cref="UnboundDecision.TryCommit"/>
    public async Task<Result<TValue>> TryCommit(
        DcbQuery query, long expectedPosition, CancellationToken cancellationToken)
    {
        try
        {
            return await Commit(query, expectedPosition, cancellationToken);
        }
        catch (DcbConflictException exception)
        {
            return Result<TValue>.Fail(PipelineInternals.Conflict(exception));
        }
    }

    /// <inheritdoc cref="UnboundDecision.CommitUnconditionally"/>
    public async Task<Result<TValue>> CommitUnconditionally(CancellationToken cancellationToken)
    {
        var store = PipelineInternals.Require(_store);
        var decision = await _decide!(cancellationToken);
        return await store.Persist(decision, DcbQuery.Empty, null, cancellationToken);
    }
}
