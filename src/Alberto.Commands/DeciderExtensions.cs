using Alberto;
using System.Diagnostics.CodeAnalysis;

namespace Alberto.Commands;

/// <summary>
/// Extension methods for the DCB decide-and-append pattern on <see cref="IEventStore"/>.
/// </summary>
public static class DeciderExtensions
{
    /// <summary>
    /// Loads state from the event store, applies a decision function, and appends resulting events.
    /// Handles the full DCB cycle: stream → reconstitute → decide → append with conflict check.
    /// </summary>
    /// <remarks>
    /// This overload reconstitutes state using raw JSON deserialization (no upcaster chain).
    /// <b>If any event in the boundary stream was stored at an older schema version than the handler
    /// type expects, this overload throws <see cref="InvalidOperationException"/> at reconstitution time</b>
    /// rather than silently producing stale state.
    /// When your application uses schema versioning, use the overload that takes an
    /// <see cref="EventSerializer"/> argument — it applies the upcaster chain automatically.
    /// </remarks>
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
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by the required EventSerializer parameter, not by " +
                        "the optional cancellation token: a CancellationToken never converts to an " +
                        "EventSerializer, so no call can bind to both.")]
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

    /// <summary>
    /// Overload that threads an <see cref="EventSerializer"/> through reconstitution so that
    /// registered upcasters are applied before the evolver sees each event.
    /// Prefer this overload over the five-argument form when your application uses schema
    /// versioning — without a serializer the evolver falls back to raw JSON deserialization,
    /// which silently bypasses the upcaster chain.
    /// </summary>
    /// <typeparam name="TState">The state type produced by the evolver. Must have a parameterless constructor.</typeparam>
    /// <param name="eventStore">The event store to read from and append to.</param>
    /// <param name="boundary">The DCB boundary query for both reading and conflict checking.</param>
    /// <param name="evolver">Folds boundary events into state.</param>
    /// <param name="decide">Pure function that returns a <see cref="Decision"/>.</param>
    /// <param name="toEventToPersist">Maps each <see cref="IEvent"/> to an <see cref="IEventToPersist"/>.</param>
    /// <param name="serializer">
    /// Used to deserialize envelopes (including upcasting) during reconstitution.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by the required EventSerializer parameter, not by " +
                        "the optional cancellation token: a CancellationToken never converts to an " +
                        "EventSerializer, so no call can bind to both.")]
    public static async Task<Result> DecideAndAppendAsync<TState>(
        this IEventStore eventStore,
        DcbQuery boundary,
        Evolver<TState> evolver,
        Func<TState, Decision> decide,
        Func<IEvent, IEventToPersist> toEventToPersist,
        EventSerializer serializer,
        CancellationToken ct = default)
        where TState : new()
    {
        var envelopes = await eventStore.StreamAsync(boundary, cancellationToken: ct);
        var state = evolver.Reconstitute(envelopes, default, serializer.Deserialize);
        var lastPosition = envelopes.Count > 0 ? envelopes.Max(e => e.GlobalPosition) : 0L;

        var decision = decide(state);
        if (decision.IsError)
            return Result.Fail(decision.Problems);

        if (decision.Events.Count > 0)
            await eventStore.AppendAsync(decision.Events.Select(toEventToPersist), boundary, lastPosition, ct);

        return Result.Success();
    }
}
