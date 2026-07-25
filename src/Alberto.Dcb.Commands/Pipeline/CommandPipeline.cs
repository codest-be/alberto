namespace Alberto.Dcb;

/// <summary>
/// The entry stage of the command pipeline, returned by <see cref="AlbertoStore.Handle{TCommand}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The guided path establishes a DCB boundary before deciding, so the append is
/// conflict-checked against exactly the events the decision was based on:
/// </para>
/// <code>
/// await store.Handle(command)
///     .Validate(Rules.Confirm)                 // optional, chainable
///     .Enrich((cmd, ct) =&gt; LookupAsync(cmd, ct))  // optional, async, runs once
///     .Load(boundary, evolver)                 // folds state AND captures the position
///     .Decide(Actions.Confirm)                 // synchronous and pure
///     .RetryOnConflict(3)                      // optional
///     .Commit(ct);
/// </code>
/// <para>
/// Everything is deferred: nothing touches the event store until a terminal
/// <c>Commit</c> / <c>TryCommit</c>.
/// </para>
/// </remarks>
public readonly struct CommandPipeline<TCommand>
{
    private readonly AlbertoStore? _store;
    private readonly Func<CancellationToken, Task<Staged<TCommand>>>? _prepare;

    internal CommandPipeline(AlbertoStore store, TCommand command)
        : this(store, _ => Task.FromResult(Staged<TCommand>.Ok(command)))
    {
    }

    internal CommandPipeline(AlbertoStore store, Func<CancellationToken, Task<Staged<TCommand>>> prepare)
    {
        _store = store;
        _prepare = prepare;
    }

    private (AlbertoStore Store, Func<CancellationToken, Task<Staged<TCommand>>> Prepare) Unwrap() =>
        (PipelineInternals.Require(_store), _prepare!);

    /// <summary>
    /// Checks the command before any IO. May be called zero, one, or many times —
    /// the first failure short-circuits the rest of the pipeline.
    /// </summary>
    public CommandPipeline<TCommand> Validate(Func<TCommand, Result> validate)
    {
        var (store, prepare) = Unwrap();
        return new CommandPipeline<TCommand>(store, async ct =>
        {
            var staged = await prepare(ct);
            if (staged.HasProblems)
                return staged;

            var result = validate(staged.Command);
            return result.IsFailure ? Staged<TCommand>.Failed(result.Problems) : staged;
        });
    }

    /// <summary>Transforms the command synchronously.</summary>
    public CommandPipeline<TNew> Map<TNew>(Func<TCommand, TNew> map)
    {
        var (store, prepare) = Unwrap();
        return new CommandPipeline<TNew>(store, async ct =>
        {
            var staged = await prepare(ct);
            return staged.HasProblems
                ? Staged<TNew>.Failed(staged.Problems!)
                : Staged<TNew>.Ok(map(staged.Command));
        });
    }

    /// <summary>
    /// Performs IO the decision needs — an external lookup, a rate quote, a policy fetch —
    /// and folds the answer into the command.
    /// </summary>
    /// <remarks>
    /// Enrichment runs <em>before</em> <c>Load</c>, so it stays outside the consistency
    /// window, and it runs at most once even under <c>RetryOnConflict</c>. This is why
    /// <c>Decide</c> is synchronous: a decision that awaits is a decision a retry would
    /// repeat.
    /// </remarks>
    public CommandPipeline<TNew> Enrich<TNew>(Func<TCommand, CancellationToken, Task<TNew>> enrich)
    {
        var (store, prepare) = Unwrap();
        return new CommandPipeline<TNew>(store, async ct =>
        {
            var staged = await prepare(ct);
            return staged.HasProblems
                ? Staged<TNew>.Failed(staged.Problems!)
                : Staged<TNew>.Ok(await enrich(staged.Command, ct));
        });
    }

    /// <inheritdoc cref="Enrich{TNew}(Func{TCommand, CancellationToken, Task{TNew}})"/>
    public CommandPipeline<TNew> Enrich<TNew>(Func<TCommand, Task<TNew>> enrich)
    {
        ArgumentNullException.ThrowIfNull(enrich);
        return Enrich((command, _) => enrich(command));
    }

    /// <summary>
    /// Reads the boundary, folding it into state with <paramref name="apply"/>, and captures
    /// the position it was read at. The resulting pipeline can <c>Commit(ct)</c>.
    /// </summary>
    public BoundPipeline<TCommand, TState> Load<TState>(
        DcbQuery boundary,
        TState initial,
        Func<TState, IEvent, TState> apply) =>
        Load(_ => boundary, initial, apply);

    /// <inheritdoc cref="Load{TState}(DcbQuery, TState, Func{TState, IEvent, TState})"/>
    /// <param name="boundary">Derives the boundary from the command, after enrichment.</param>
    /// <param name="initial">The zero state the fold starts from.</param>
    /// <param name="apply">Folds one event into the state.</param>
    public BoundPipeline<TCommand, TState> Load<TState>(
        Func<TCommand, DcbQuery> boundary,
        TState initial,
        Func<TState, IEvent, TState> apply)
    {
        var (store, prepare) = Unwrap();
        return new BoundPipeline<TCommand, TState>(store, prepare, async (command, ct) =>
        {
            var query = boundary(command);
            var (state, position) = await store.FoldWithPosition(query, initial, apply, ct);
            return (state, new DcbBoundary(query, position));
        });
    }

    /// <summary>
    /// Reads the boundary and reconstitutes state with an <see cref="Evolver{TState}"/> —
    /// the same evolver the rest of the domain uses.
    /// </summary>
    public BoundPipeline<TCommand, TState> Load<TState>(DcbQuery boundary, Evolver<TState> evolver)
        where TState : new() =>
        Load(_ => boundary, evolver);

    /// <inheritdoc cref="Load{TState}(DcbQuery, Evolver{TState})"/>
    public BoundPipeline<TCommand, TState> Load<TState>(
        Func<TCommand, DcbQuery> boundary,
        Evolver<TState> evolver)
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(evolver);

        var (store, prepare) = Unwrap();
        return new BoundPipeline<TCommand, TState>(store, prepare, async (command, ct) =>
        {
            var query = boundary(command);
            var (state, position) = await store.ReconstituteWithPosition(query, evolver, ct);
            return (state, new DcbBoundary(query, position));
        });
    }

    /// <summary>
    /// Reads the boundary and reconstitutes state with the <see cref="Evolver{TState}"/>
    /// registered in DI. Requires the store to have been resolved from a service provider.
    /// </summary>
    public BoundPipeline<TCommand, TState> Load<TState>(DcbQuery boundary)
        where TState : new() =>
        Load<TState>(_ => boundary);

    /// <inheritdoc cref="Load{TState}(DcbQuery)"/>
    public BoundPipeline<TCommand, TState> Load<TState>(Func<TCommand, DcbQuery> boundary)
        where TState : new()
    {
        var (store, prepare) = Unwrap();
        return new BoundPipeline<TCommand, TState>(store, prepare, async (command, ct) =>
        {
            var evolver = store.ResolveEvolver<TState>();
            var query = boundary(command);
            var (state, position) = await store.ReconstituteWithPosition(query, evolver, ct);
            return (state, new DcbBoundary(query, position));
        });
    }

    /// <summary>
    /// Loads state from somewhere that is not a DCB boundary — a read model, another service.
    /// No consistency boundary is established, so the caller must supply the query and position
    /// at commit time, or commit unconditionally.
    /// </summary>
    public UnboundPipeline<TCommand, TState> LoadUnbound<TState>(
        Func<TCommand, CancellationToken, Task<TState>> load)
    {
        var (store, prepare) = Unwrap();
        return new UnboundPipeline<TCommand, TState>(store, prepare, load);
    }

    /// <inheritdoc cref="LoadUnbound{TState}(Func{TCommand, CancellationToken, Task{TState}})"/>
    public UnboundPipeline<TCommand, TState> LoadUnbound<TState>(Func<TCommand, Task<TState>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        return LoadUnbound((command, _) => load(command));
    }

    /// <summary>
    /// Decides from the command alone, without reading state. No boundary is established.
    /// </summary>
    public UnboundDecision Decide(Func<TCommand, Decision> decide)
    {
        var (store, prepare) = Unwrap();
        var once = new Once<Staged<TCommand>>(prepare);
        return new UnboundDecision(store, async ct =>
        {
            var staged = await once.Get(ct);
            return staged.HasProblems ? Decision.Fail(staged.Problems!) : decide(staged.Command);
        });
    }

    /// <inheritdoc cref="Decide(Func{TCommand, Decision})"/>
    public UnboundDecision<TValue> Decide<TValue>(Func<TCommand, Decision<TValue>> decide)
    {
        var (store, prepare) = Unwrap();
        var once = new Once<Staged<TCommand>>(prepare);
        return new UnboundDecision<TValue>(store, async ct =>
        {
            var staged = await once.Get(ct);
            return staged.HasProblems ? Decision<TValue>.Fail(staged.Problems!) : decide(staged.Command);
        });
    }
}
