namespace Alberto.Dcb.Subscriptions.Pipeline;

/// <summary>
/// Defines a filter in the consume pipeline.
/// Filters wrap event processing to add cross-cutting concerns like
/// telemetry, logging, error handling, or transaction management.
/// </summary>
[Obsolete("Use ConsumeMiddleware instead. IConsumeFilter will be removed in a future version.")]
public interface IConsumeFilter
{
    /// <summary>
    /// Called when events are being consumed.
    /// </summary>
    /// <param name="events">The events being processed.</param>
    /// <param name="context">The consume context.</param>
    /// <param name="next">The next filter or final processing action.</param>
    /// <param name="ct">Cancellation token.</param>
    Task OnConsumingAsync(
        IReadOnlyList<IEventEnvelope> events,
        ConsumeContext context,
        Func<Task> next,
        CancellationToken ct = default);
}
