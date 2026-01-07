namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Typed reaction handler interface. Implement this for each event type
/// that your reactor handles.
/// </summary>
/// <typeparam name="TEvent">The event type to handle.</typeparam>
public interface IReact<in TEvent> where TEvent : IEvent
{
    /// <summary>
    /// React to an event. This is where side effects happen.
    /// The event is already deserialized.
    /// </summary>
    Task ReactAsync(TEvent @event, IEventEnvelope envelope, CancellationToken ct);
}
