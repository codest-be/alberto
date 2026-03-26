namespace Alberto.Dcb.Subscriptions.Pipeline;

/// <summary>
/// Executes the consume filter pipeline.
/// </summary>
[Obsolete("Use ConsumeMiddleware instead. IConsumeFilterPipeline will be removed in a future version.")]
internal interface IConsumeFilterPipeline
{
    /// <summary>
    /// Executes all registered filters around the processing action.
    /// </summary>
    /// <param name="events">The events being processed.</param>
    /// <param name="context">The consume context.</param>
    /// <param name="processingAction">The final processing action to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ExecuteAsync(
        IReadOnlyList<IEventEnvelope> events,
        ConsumeContext context,
        Func<Task> processingAction,
        CancellationToken ct = default);
}
