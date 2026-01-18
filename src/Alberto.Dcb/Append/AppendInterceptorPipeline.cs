namespace Alberto.Dcb.Append;

/// <summary>
/// Builds and executes the append interceptor pipeline.
/// Interceptors are executed in registration order (first registered = outermost wrapper).
/// </summary>
internal sealed class AppendInterceptorPipeline(IEnumerable<IAppendInterceptor> interceptors)
    : IAppendInterceptorPipeline
{
    private readonly IReadOnlyList<IAppendInterceptor> _interceptors = interceptors.ToList();

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> ExecuteAsync(
        AppendContext context,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>> appendAction,
        CancellationToken ct = default)
    {
        if (_interceptors.Count == 0)
            return appendAction(context);

        // Build pipeline: reverse so first registered is outermost
        var pipeline = _interceptors
            .Reverse()
            .Aggregate(
                appendAction,
                (next, interceptor) => ctx => interceptor.OnAppendingAsync(ctx, next, ct));

        return pipeline(context);
    }
}
