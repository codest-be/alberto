using Alberto.Subscriptions;

namespace Alberto;

/// <summary>
/// Collects the event types one multi-type reactor handles. Passed to the callback of
/// <see cref="AlbertoModuleBuilderExtensions.React{THandler}"/>.
/// </summary>
/// <typeparam name="THandler">The handler class, resolved from a fresh scope per event.</typeparam>
public sealed class ReactorBuilder<THandler>
    where THandler : class
{
    internal Dictionary<string, Func<THandler, IEventEnvelope, EventSerializer?, ReactorContext, CancellationToken, Task>> Handlers { get; } =
        new(StringComparer.Ordinal);

    internal ReactorBuilder() { }

    /// <summary>
    /// Handles <typeparamref name="TEvent"/> by calling <paramref name="handler"/> on the resolved handler.
    /// </summary>
    /// <typeparam name="TEvent">The event type to handle. Each type can be registered once per reactor.</typeparam>
    /// <param name="handler">Receives the handler instance, the event and a cancellation token.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEvent"/> is already registered on this reactor.</exception>
    public ReactorBuilder<THandler> On<TEvent>(Func<THandler, TEvent, CancellationToken, Task> handler)
        where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return On<TEvent>((h, e, _, ct) => handler(h, e, ct));
    }

    /// <summary>
    /// Handles <typeparamref name="TEvent"/> by calling <paramref name="handler"/> on the resolved handler,
    /// with the <see cref="ReactorContext"/> of the event.
    /// </summary>
    /// <typeparam name="TEvent">The event type to handle. Each type can be registered once per reactor.</typeparam>
    /// <param name="handler">Receives the handler instance, the event, its context and a cancellation token.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEvent"/> is already registered on this reactor.</exception>
    public ReactorBuilder<THandler> On<TEvent>(Func<THandler, TEvent, ReactorContext, CancellationToken, Task> handler)
        where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var typeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        if (!Handlers.TryAdd(typeId, (h, envelope, serializer, context, ct) =>
                handler(h, EventEnvelopeExtensions.DeserializeEvent<TEvent>(envelope, serializer), context, ct)))
        {
            throw new InvalidOperationException(
                $"Event type '{typeof(TEvent).Name}' is already handled by this reactor. Register each event type once.");
        }

        return this;
    }
}
