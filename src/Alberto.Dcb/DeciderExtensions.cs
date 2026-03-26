namespace Alberto.Dcb;

public static class DeciderExtensions
{
    /// <summary>
    /// Loads state from the event store, applies a decision function, and appends resulting events.
    /// Handles the full DCB cycle: stream → reconstitute → decide → append with conflict check.
    /// </summary>
    public static async Task<DecisionResult<TEvent>> DecideAndAppendAsync<TState, TEvent>(
        this IEventStoreBackend backend,
        DcbQuery boundary,
        Evolver<TState> evolver,
        Func<TState, DecisionResult<TEvent>> decide,
        Func<TEvent, IEventToPersist> toEventToPersist,
        CancellationToken ct = default)
        where TState : new()
        where TEvent : IEvent
    {
        var events = await backend.Stream(boundary, cancellationToken: ct);
        var state = evolver.Reconstitute(events);
        var lastPosition = events.Count > 0 ? events.Max(e => e.GlobalPosition) : 0L;

        var result = decide(state);
        if (result is DecisionResult<TEvent>.Fail)
            return result;

        var ok = (DecisionResult<TEvent>.Ok)result;
        var toPersist = ok.Events.Select(toEventToPersist);
        await backend.Append(toPersist, boundary, lastPosition, ct);
        return result;
    }
}
