namespace Alberto.Subscriptions;

/// <summary>
/// Pre-composes middleware lists into single delegates at construction time,
/// and dispatches chains inline for test/one-shot use.
/// A single generic core (<see cref="BuildCore{TContext,TMiddleware}"/>) serves
/// both <see cref="ConsumeMiddleware"/> and <see cref="BatchConsumeMiddleware"/>
/// so adding a new cross-cutting concern costs one implementation instead of two.
/// </summary>
internal static class MiddlewareRunner
{
    // -------------------------------------------------------------------------
    // Single-event overloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pre-composes a <see cref="ConsumeMiddleware"/> list into a single delegate
    /// at construction time. Calling the returned delegate per-event avoids
    /// allocating the recursive <c>Dispatch</c> async state machine that the
    /// naïve indexed-recursion approach creates for every middleware layer on
    /// every event.
    /// </summary>
    /// <param name="middlewares">The ordered middleware list (outermost first).</param>
    /// <returns>
    /// A delegate that, given a <see cref="ConsumeEventContext"/> and a terminal
    /// <c>Func&lt;Task&gt;</c>, runs the full middleware chain.
    /// </returns>
    public static Func<ConsumeEventContext, Func<Task>, Task> Build(
        IReadOnlyList<ConsumeMiddleware> middlewares) =>
        BuildCore<ConsumeEventContext, ConsumeMiddleware>(middlewares, static (mw, ctx, next) => mw(ctx, next));

    /// <summary>
    /// Runs the <see cref="ConsumeMiddleware"/> chain inline (allocates a
    /// per-call recursive dispatch). Prefer
    /// <see cref="Build(IReadOnlyList{ConsumeMiddleware})"/> when the same
    /// middleware list is used repeatedly.
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

    // -------------------------------------------------------------------------
    // Batch overloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pre-composes a <see cref="BatchConsumeMiddleware"/> list into a single
    /// delegate at construction time. Uses the same fold algorithm as the
    /// single-event overload via <see cref="BuildCore{TContext,TMiddleware}"/>.
    /// </summary>
    /// <param name="middlewares">The ordered batch middleware list (outermost first).</param>
    /// <returns>
    /// A delegate that, given a <see cref="BatchConsumeContext"/> and a terminal
    /// <c>Func&lt;Task&gt;</c>, runs the full batch middleware chain.
    /// </returns>
    public static Func<BatchConsumeContext, Func<Task>, Task> Build(
        IReadOnlyList<BatchConsumeMiddleware> middlewares) =>
        BuildCore<BatchConsumeContext, BatchConsumeMiddleware>(middlewares, static (mw, ctx, next) => mw(ctx, next));

    /// <summary>
    /// Runs the <see cref="BatchConsumeMiddleware"/> chain inline (allocates a
    /// per-call recursive dispatch). Prefer
    /// <see cref="Build(IReadOnlyList{BatchConsumeMiddleware})"/> when the same
    /// middleware list is used repeatedly.
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

    // -------------------------------------------------------------------------
    // Generic core — single implementation used by all typed overloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Folds <paramref name="middlewares"/> from innermost to outermost so each
    /// wrapper closes over the one below it. <paramref name="invoke"/> converts
    /// a <typeparamref name="TMiddleware"/> into a pipeline call — its static
    /// lambda form (<c>static (mw, ctx, next) => mw(ctx, next)</c>) is
    /// allocation-free per event because no variables are captured.
    /// N closures are allocated here (once, at build time) rather than on every
    /// event dispatch.
    /// </summary>
    private static Func<TContext, Func<Task>, Task> BuildCore<TContext, TMiddleware>(
        IReadOnlyList<TMiddleware> middlewares,
        Func<TMiddleware, TContext, Func<Task>, Task> invoke)
    {
        if (middlewares.Count == 0)
            return static (_, terminal) => terminal();

        Func<TContext, Func<Task>, Task> chain = static (_, terminal) => terminal();
        for (var i = middlewares.Count - 1; i >= 0; i--)
        {
            var mw = middlewares[i];
            var inner = chain;
            chain = (ctx, terminal) => invoke(mw, ctx, () => inner(ctx, terminal));
        }
        return chain;
    }
}
