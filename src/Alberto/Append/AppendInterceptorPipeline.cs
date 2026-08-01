namespace Alberto.Append;

/// <summary>
/// Builds and executes the append interceptor pipeline.
/// Interceptors are executed in registration order (first registered = outermost wrapper).
/// The composed delegate chain is built once at construction and reused on every append,
/// avoiding per-call list reversal and LINQ aggregation.
/// </summary>
internal sealed class AppendInterceptorPipeline : IAppendInterceptorPipeline
{
    // Null when no interceptors are registered (fast-path: invoke appendAction directly).
    // When non-null: given (appendAction, ct), returns the fully composed pipeline delegate.
    private readonly Func<
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>>,
        CancellationToken,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>>>? _composed;

    public AppendInterceptorPipeline(IEnumerable<IAppendInterceptor> interceptors)
    {
        _composed = Build(interceptors.ToList());
    }

    /// <summary>
    /// Builds the compositor once. Returns <see langword="null"/> when the list is empty so
    /// <see cref="ExecuteAsync"/> can skip invocation entirely.
    /// </summary>
    private static Func<
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>>,
        CancellationToken,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>>>?
        Build(IReadOnlyList<IAppendInterceptor> interceptors)
    {
        if (interceptors.Count == 0)
            return null;

        // Seed: identity — return the append action unchanged.
        Func<
            Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>>,
            CancellationToken,
            Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>>> composed
            = static (action, _) => action;

        // Walk in reverse so the first-registered interceptor ends up as the outermost wrapper,
        // matching the original Reverse().Aggregate() ordering.
        for (var i = interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = interceptors[i]; // capture the current interceptor by value
            var inner = composed;              // capture the current composition by value

            composed = (action, ct) => ctx => interceptor.OnAppendingAsync(ctx, inner(action, ct), ct);
        }

        return composed;
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> ExecuteAsync(
        AppendContext context,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>> appendAction,
        CancellationToken ct = default)
    {
        if (_composed is null)
            return appendAction(context);

        return _composed(appendAction, ct)(context);
    }
}
