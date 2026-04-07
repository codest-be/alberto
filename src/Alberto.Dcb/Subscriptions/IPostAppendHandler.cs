namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Handler that runs synchronously during <see cref="IEventStore.AppendAsync"/>,
/// after event persistence and inline projections.
/// Registered via <see cref="DcbModuleBuilderExtensions.ReactTo{TEvent}"/> with <see cref="ReactorMode.Sync"/>.
/// </summary>
public interface IPostAppendHandler
{
    /// <summary>
    /// The event types this handler processes.
    /// </summary>
    IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>
    /// Processes events after they have been appended.
    /// </summary>
    /// <param name="events">The appended events to process.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ProcessAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct);
}
