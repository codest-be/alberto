using Alberto.Dcb;
namespace Alberto.Commands;

/// <summary>
/// The command-side entry point: starts a pipeline, folds boundary events into state,
/// and appends the resulting events under that boundary.
/// </summary>
/// <param name="eventStore">The backing event store for this module.</param>
/// <param name="serializer">Serializes events and extracts their tags.</param>
/// <param name="services">
/// Optional. When present, <c>Load&lt;TState&gt;(boundary)</c> can resolve the
/// <see cref="Evolver{TState}"/> from DI instead of taking one as an argument.
/// </param>
public sealed class AlbertoStore(
    IEventStore eventStore,
    EventSerializer serializer,
    IServiceProvider? services = null)
{
    public CommandPipeline<TCommand> Handle<TCommand>(TCommand command) => new(this, command);

    internal Evolver<TState> ResolveEvolver<TState>()
        where TState : new()
    {
        if (services is null)
            throw new InvalidOperationException(
                $"Load<{typeof(TState).Name}>(boundary) resolves Evolver<{typeof(TState).Name}> from DI, " +
                "but this AlbertoStore was constructed without a service provider. " +
                "Resolve the store from DI, or pass the evolver explicitly: Load(boundary, evolver).");

        return services.GetService(typeof(Evolver<TState>)) as Evolver<TState>
               ?? throw new InvalidOperationException(
                   $"No Evolver<{typeof(TState).Name}> is registered. " +
                   $"Register one — services.AddSingleton<Evolver<{typeof(TState).Name}>, YourEvolver>() — " +
                   "or pass it explicitly: Load(boundary, evolver).");
    }

    internal async Task<Result> Persist(
        Decision decision,
        DcbQuery query,
        long? expectedPosition,
        CancellationToken cancellationToken)
    {
        if (decision.IsError)
            return Result.Fail(decision.Problems);

        await AppendAsync(query, decision.Events, expectedPosition, cancellationToken);
        return Result.Success();
    }

    internal async Task<Result<T>> Persist<T>(
        Decision<T> decision,
        DcbQuery query,
        long? expectedPosition,
        CancellationToken cancellationToken)
    {
        if (decision.IsError)
            return Result<T>.Fail(decision.Problems);

        await AppendAsync(query, decision.Events, expectedPosition, cancellationToken);
        return Result<T>.Success(decision.Value);
    }

    public async Task<TState> Fold<TState>(
        DcbQuery query,
        TState initial,
        Func<TState, IEvent, TState> apply,
        CancellationToken cancellationToken)
    {
        var result = await FoldWithPosition(query, initial, apply, cancellationToken);
        return result.State;
    }

    /// <summary>
    /// Reconstitutes state from the boundary using an <see cref="Evolver{TState}"/>,
    /// returning the position the boundary was read at.
    /// </summary>
    internal async Task<(TState State, long LastPosition)> ReconstituteWithPosition<TState>(
        DcbQuery query,
        Evolver<TState> evolver,
        CancellationToken cancellationToken)
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(evolver);

        var envelopes = await eventStore.StreamAsync(query, cancellationToken: cancellationToken);

        var lastPosition = 0L;
        foreach (var envelope in envelopes)
        {
            if (envelope.GlobalPosition > lastPosition)
                lastPosition = envelope.GlobalPosition;
        }

        // Route through EventSerializer (which applies the upcaster chain) rather than
        // letting Evolver fall back to raw JsonSerializer — that path silently bypasses
        // upcasting and causes decisions to see stale v1 event shapes.
        return (evolver.Reconstitute(envelopes, default, serializer.Deserialize), lastPosition);
    }

    internal async Task<(TState State, long LastPosition)> FoldWithPosition<TState>(
        DcbQuery query,
        TState initial,
        Func<TState, IEvent, TState> apply,
        CancellationToken cancellationToken)
    {
        var events = await eventStore.StreamAsync(
            query,
            cancellationToken: cancellationToken);

        var state = initial;
        var lastPosition = 0L;

        foreach (var envelope in events)
        {
            state = apply(state, serializer.Deserialize(envelope));
            lastPosition = envelope.GlobalPosition;
        }

        return (state, lastPosition);
    }

    private async Task AppendAsync(
        DcbQuery query,
        IReadOnlyList<IEvent> events,
        long? expectedPosition,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return;

        await eventStore.AppendAsync(
            events.Select(@event => new EventToPersist
            {
                EventType = EventType.FromType(@event.GetType()),
                Tags = serializer.ExtractTags(@event),
                EventData = serializer.Serialize(@event),
                Metadata = new Dictionary<string, string>()
            }).ToArray(),
            query,
            expectedPosition,
            cancellationToken: cancellationToken);
    }
}
