namespace Alberto.Dcb.Subscriptions;

internal static class BatchMiddlewareRunner
{
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
