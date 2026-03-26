namespace Alberto.Dcb.Subscriptions.Pipeline;

/// <summary>
/// Builds and executes the consume filter pipeline.
/// Filters are executed in registration order (first registered = outermost wrapper).
/// </summary>
[Obsolete("Use ConsumeMiddleware instead. ConsumeFilterPipeline will be removed in a future version.")]
internal sealed class ConsumeFilterPipeline(IEnumerable<IConsumeFilter> filters) : IConsumeFilterPipeline
{
    private readonly IReadOnlyList<IConsumeFilter> _filters = filters.ToList();

    /// <inheritdoc />
    public Task ExecuteAsync(
        IReadOnlyList<IEventEnvelope> events,
        ConsumeContext context,
        Func<Task> processingAction,
        CancellationToken ct = default)
    {
        if (_filters.Count == 0)
            return processingAction();

        // Build pipeline: reverse so first registered is outermost
        var pipeline = _filters
            .Reverse()
            .Aggregate(
                processingAction,
                (next, filter) => () => filter.OnConsumingAsync(events, context, next, ct));

        return pipeline();
    }
}
