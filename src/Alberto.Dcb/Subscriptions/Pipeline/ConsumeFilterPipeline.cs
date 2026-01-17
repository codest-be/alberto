namespace Alberto.Dcb.Subscriptions.Pipeline;

/// <summary>
/// Builds and executes the consume filter pipeline.
/// Filters are executed in registration order (first registered = outermost wrapper).
/// </summary>
internal sealed class ConsumeFilterPipeline : IConsumeFilterPipeline
{
    private readonly IReadOnlyList<IConsumeFilter> _filters;

    public ConsumeFilterPipeline(IEnumerable<IConsumeFilter> filters)
    {
        _filters = filters.ToList();
    }

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
