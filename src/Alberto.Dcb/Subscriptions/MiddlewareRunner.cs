namespace Alberto.Dcb.Subscriptions;

internal static class MiddlewareRunner
{
    /// <summary>
    /// Pre-composes a middleware list into a single delegate at construction time.
    /// Calling the returned delegate per-event avoids allocating the recursive
    /// <c>Dispatch</c> async state machine that the naïve indexed-recursion approach
    /// creates for every middleware layer on every event.
    /// </summary>
    /// <param name="middlewares">The ordered middleware list (outermost first).</param>
    /// <returns>
    /// A delegate that, given a <see cref="ConsumeEventContext"/> and a terminal
    /// <c>Func&lt;Task&gt;</c>, runs the full middleware chain.
    /// </returns>
    public static Func<ConsumeEventContext, Func<Task>, Task> Build(
        IReadOnlyList<ConsumeMiddleware> middlewares)
    {
        if (middlewares.Count == 0)
            return static (_, terminal) => terminal();

        // Fold from innermost to outermost so each wrapper closes over the one below it.
        // N closures are allocated here (once, at build time) rather than on every event.
        Func<ConsumeEventContext, Func<Task>, Task> chain = static (_, terminal) => terminal();
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
        ConsumeEventContext context,
        IReadOnlyList<ConsumeMiddleware> middlewares,
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
