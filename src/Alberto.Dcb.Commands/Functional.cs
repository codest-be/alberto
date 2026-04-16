namespace Alberto.Dcb;

internal readonly record struct DcbBoundary(DcbQuery Query, long? ExpectedPosition);

public readonly struct CommandPipeline<TCommand>
{
    private readonly AlbertoStore _store;
    private readonly TCommand _command;
    private readonly IReadOnlyList<Problem>? _problems;

    internal CommandPipeline(AlbertoStore store, TCommand command, IReadOnlyList<Problem>? problems = null)
    {
        _store = store;
        _command = command;
        _problems = problems;
    }

    public ValidatedPipeline<TCommand> Validate(Func<TCommand, Result> validate)
    {
        if (_problems is { Count: > 0 })
            return new ValidatedPipeline<TCommand>(_store, _command, _problems);

        var result = validate(_command);
        return result.IsFailure
            ? new ValidatedPipeline<TCommand>(_store, _command, result.Problems)
            : new ValidatedPipeline<TCommand>(_store, _command);
    }

    public ValidatedPipeline<TCommand> NoValidation() => new(_store, _command, _problems);

    public CommandPipeline<TNew> Map<TNew>(Func<TCommand, TNew> map) =>
        _problems is { Count: > 0 }
            ? new CommandPipeline<TNew>(_store, default!, _problems)
            : new CommandPipeline<TNew>(_store, map(_command));
}

public readonly struct ValidatedPipeline<TCommand>
{
    private readonly AlbertoStore _store;
    private readonly TCommand _command;
    private readonly IReadOnlyList<Problem>? _problems;

    internal ValidatedPipeline(AlbertoStore store, TCommand command, IReadOnlyList<Problem>? problems = null)
    {
        _store = store;
        _command = command;
        _problems = problems;
    }

    public LoadedPipeline<TCommand, TState> Load<TState>(
        Func<TCommand, CancellationToken, Task<TState>> load) =>
        new(_store, _command, async (command, ct) => (await load(command, ct), (DcbBoundary?)null), _problems);

    public LoadedPipeline<TCommand, TState> Load<TState>(
        Func<TCommand, Task<TState>> load) =>
        new(_store, _command, async (command, _) => (await load(command), (DcbBoundary?)null), _problems);

    public LoadedPipeline<TCommand, TState> Load<TState>(
        Func<TCommand, Task<TState>> load,
        Func<TState, DcbQuery> query) =>
        new(
            _store,
            _command,
            async (command, _) =>
            {
                var state = await load(command);
                return (state, new DcbBoundary(query(state), null));
            },
            _problems);

    public LoadedPipeline<TCommand, TState> Load<TState>(
        Func<TCommand, Task<TState>> load,
        Func<TState, DcbQuery> query,
        Func<TState, long> expectedPosition) =>
        new(
            _store,
            _command,
            async (command, _) =>
            {
                var state = await load(command);
                return (state, new DcbBoundary(query(state), expectedPosition(state)));
            },
            _problems);

    public LoadedPipeline<TCommand, TState> Load<TState>(
        DcbQuery query, TState initial, Func<TState, IEvent, TState> apply)
    {
        var store = _store;
        return new(
            store,
            _command,
            async (_, ct) =>
            {
                var (state, lastPosition) = await store.FoldWithPosition(query, initial, apply, ct);
                return (state, new DcbBoundary(query, lastPosition));
            },
            _problems);
    }

    public DecidedPipeline<TValue> Decide<TValue>(
        Func<TCommand, Decision<TValue>> decide) =>
        _problems is { Count: > 0 }
            ? new DecidedPipeline<TValue>(_store, Decision<TValue>.Fail(_problems))
            : new DecidedPipeline<TValue>(_store, decide(_command));

    public DecidedPipeline Decide(
        Func<TCommand, Decision> decide) =>
        _problems is { Count: > 0 }
            ? new DecidedPipeline(_store, Decision.Fail(_problems))
            : new DecidedPipeline(_store, decide(_command));
}

public readonly struct LoadedPipeline<TCommand, TState>
{
    private readonly AlbertoStore _store;
    private readonly TCommand _command;
    private readonly Func<TCommand, CancellationToken, Task<(TState State, DcbBoundary? Boundary)>> _load;
    private readonly IReadOnlyList<Problem>? _problems;

    internal LoadedPipeline(
        AlbertoStore store,
        TCommand command,
        Func<TCommand, CancellationToken, Task<(TState State, DcbBoundary? Boundary)>> load,
        IReadOnlyList<Problem>? problems)
    {
        _store = store;
        _command = command;
        _load = load;
        _problems = problems;
    }

    public DecidedPipeline<TValue> Decide<TValue>(
        Func<TCommand, TState, Decision<TValue>> decide)
    {
        if (_problems is { Count: > 0 })
            return new DecidedPipeline<TValue>(_store, Decision<TValue>.Fail(_problems));

        var command = _command;
        var load = _load;
        return new DecidedPipeline<TValue>(_store, async ct =>
        {
            var (state, boundary) = await load(command, ct);
            return (decide(command, state), boundary);
        });
    }

    public DecidedPipeline Decide(
        Func<TCommand, TState, Decision> decide)
    {
        if (_problems is { Count: > 0 })
            return new DecidedPipeline(_store, Decision.Fail(_problems));

        var command = _command;
        var load = _load;
        return new DecidedPipeline(_store, async ct =>
        {
            var (state, boundary) = await load(command, ct);
            return (decide(command, state), boundary);
        });
    }

    public DecidedPipeline<TValue> Decide<TValue>(
        Func<TCommand, TState, CancellationToken, Task<Decision<TValue>>> decide)
    {
        if (_problems is { Count: > 0 })
            return new DecidedPipeline<TValue>(_store, Decision<TValue>.Fail(_problems));

        var command = _command;
        var load = _load;
        return new DecidedPipeline<TValue>(_store, async ct =>
        {
            var (state, boundary) = await load(command, ct);
            return (await decide(command, state, ct), boundary);
        });
    }

    public DecidedPipeline Decide(
        Func<TCommand, TState, CancellationToken, Task<Decision>> decide)
    {
        if (_problems is { Count: > 0 })
            return new DecidedPipeline(_store, Decision.Fail(_problems));

        var command = _command;
        var load = _load;
        return new DecidedPipeline(_store, async ct =>
        {
            var (state, boundary) = await load(command, ct);
            return (await decide(command, state, ct), boundary);
        });
    }
}

public readonly struct DecidedPipeline<TValue>
{
    private readonly AlbertoStore _store;
    private readonly Func<CancellationToken, Task<(Decision<TValue> Decision, DcbBoundary? Boundary)>> _resolve;

    internal DecidedPipeline(AlbertoStore store, Decision<TValue> decision)
    {
        _store = store;
        _resolve = _ => Task.FromResult<(Decision<TValue>, DcbBoundary?)>((decision, null));
    }

    internal DecidedPipeline(
        AlbertoStore store,
        Func<CancellationToken, Task<(Decision<TValue> Decision, DcbBoundary? Boundary)>> resolve)
    {
        _store = store;
        _resolve = resolve;
    }

    public DecidedPipeline<TNew> Map<TNew>(Func<TValue, TNew> map)
    {
        var resolve = _resolve;
        return new DecidedPipeline<TNew>(_store, async ct =>
        {
            var (decision, boundary) = await resolve(ct);
            return (decision.Map(map), boundary);
        });
    }

    public async Task<Result<TValue>> Persist(DcbQuery query, CancellationToken ct)
    {
        var (decision, _) = await _resolve(ct);
        return await _store.Persist(decision, query, null, ct);
    }

    public async Task<Result<TValue>> Persist(CancellationToken ct)
    {
        var (decision, boundary) = await _resolve(ct);
        return await _store.Persist(decision, boundary!.Value.Query, boundary.Value.ExpectedPosition, ct);
    }
}

public readonly struct DecidedPipeline
{
    private readonly AlbertoStore _store;
    private readonly Func<CancellationToken, Task<(Decision Decision, DcbBoundary? Boundary)>> _resolve;

    internal DecidedPipeline(AlbertoStore store, Decision decision)
    {
        _store = store;
        _resolve = _ => Task.FromResult<(Decision, DcbBoundary?)>((decision, null));
    }

    internal DecidedPipeline(
        AlbertoStore store,
        Func<CancellationToken, Task<(Decision Decision, DcbBoundary? Boundary)>> resolve)
    {
        _store = store;
        _resolve = resolve;
    }

    public async Task<Result> Persist(DcbQuery query, CancellationToken ct)
    {
        var (decision, _) = await _resolve(ct);
        return await _store.Persist(decision, query, null, ct);
    }

    public async Task<Result> Persist(CancellationToken ct)
    {
        var (decision, boundary) = await _resolve(ct);
        return await _store.Persist(decision, boundary!.Value.Query, boundary.Value.ExpectedPosition, ct);
    }
}
