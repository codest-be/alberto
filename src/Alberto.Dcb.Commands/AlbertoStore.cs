namespace Alberto.Dcb;

public sealed class AlbertoStore(IEventStore eventStore, EventSerializer serializer)
{
    public CommandPipeline<TCommand> Handle<TCommand>(TCommand command) => new(this, command);

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

    public async Task<(TState State, long LastPosition)> FoldWithPosition<TState>(
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
