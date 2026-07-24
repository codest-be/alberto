namespace Alberto.Dcb.Subscriptions;

internal static class BatchMiddlewareRunner
{
    /// <summary>
    /// Pre-composes a batch middleware list into a single delegate at construction time.
    /// </summary>
    /// <param name="middlewares">The ordered batch middleware list (outermost first).</param>
    /// <returns>
    /// A delegate that, given a <see cref="BatchConsumeContext"/> and a terminal
    /// <c>Func&lt;Task&gt;</c>, runs the full middleware chain.
    /// </returns>
    public static Func<BatchConsumeContext, Func<Task>, Task> Build(
        IReadOnlyList<BatchConsumeMiddleware> middlewares)
    {
        if (middlewares.Count == 0)
            return static (_, terminal) => terminal();

        Func<BatchConsumeContext, Func<Task>, Task> chain = static (_, terminal) => terminal();
        for (var i = middlewares.Count - 1; i >= 0; i--)
        {
            var mw = middlewares[i];
            var inner = chain;
            chain = (ctx, terminal) => mw(ctx, () => inner(ctx, terminal));
        }
        return chain;
    }

    /// <summary>
    /// Runs the middleware chain inline (allocates a per-call recursive dispatch).
    /// Prefer <see cref="Build"/> when the same middleware list is used repeatedly.
    /// </summary>
    public static Task RunAsync(
        BatchConsumeContext context,
        IReadOnlyList<BatchConsumeMiddleware> middlewares,
        Func<Task> terminal)
    {
        return Dispatch(0);

        async Task Dispatch(int index)
        {
            if (index >= middlewares.Count)
            {
                await terminal();
                return;
            }

            await middlewares[index](context, () => Dispatch(index + 1));
        }
    }
}
