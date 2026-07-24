namespace Alberto.Dcb;

/// <summary>
/// Extension methods for the DCB decide-and-append pattern on <see cref="IEventStore"/>.
/// </summary>
public static class DeciderExtensions
{
    /// <summary>
    /// Loads state from the event store, applies a decision function, and appends resulting events.
    /// Handles the full DCB cycle: stream → reconstitute → decide → append with conflict check.
    /// </summary>
    /// <typeparam name="TState">The state type produced by the evolver. Must have a parameterless constructor.</typeparam>
    /// <param name="eventStore">The event store to read from and append to.</param>
    /// <param name="boundary">
    /// The DCB boundary query that scopes both the event stream used to reconstitute state
    /// and the conflict check performed during the append.
    /// </param>
    /// <param name="evolver">
    /// Folds the boundary events into state. Construct one concrete <see cref="Evolver{TState}"/>
    /// subclass per aggregate type; the dispatcher is cached on the type.
    /// </param>
    /// <param name="decide">
    /// Pure function that receives the reconstituted state and returns a <see cref="Decision"/>
    /// containing either the events to append or the failure problems.
    /// </param>
    /// <param name="toEventToPersist">
    /// Maps each <see cref="IEvent"/> from the decision to an <see cref="IEventToPersist"/> for storage.
    /// Supply a custom mapper that sets <see cref="IEventToPersist.EventType"/>, tags, and serialized data,
    /// or use an <see cref="EventSerializer"/> to build it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when events were appended (or the decision produced no events),
    /// or <see cref="Result.Fail(Problem)"/> when the decision function returned a failure.
    /// Throws <see cref="DcbConflictException"/> if the boundary was violated between the read and append.
    /// </returns>
    public static async Task<Result> DecideAndAppendAsync<TState>(
        this IEventStore eventStore,
        DcbQuery boundary,
        Evolver<TState> evolver,
        Func<TState, Decision> decide,
        Func<IEvent, IEventToPersist> toEventToPersist,
        CancellationToken ct = default)
        where TState : new()
    {
        var envelopes = await eventStore.StreamAsync(boundary, cancellationToken: ct);
        var state = evolver.Reconstitute(envelopes);
        var lastPosition = envelopes.Count > 0 ? envelopes.Max(e => e.GlobalPosition) : 0L;

        var decision = decide(state);
        if (decision.IsError)
            return Result.Fail(decision.Problems);

        if (decision.Events.Count > 0)
            await eventStore.AppendAsync(decision.Events.Select(toEventToPersist), boundary, lastPosition, ct);

        return Result.Success();
    }
}
