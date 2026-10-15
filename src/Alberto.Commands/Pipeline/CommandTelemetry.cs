using System.Diagnostics;
using Alberto.Telemetry;

namespace Alberto.Commands;

/// <summary>
/// Spans for one command execution: a root span around the commit, with child spans for the
/// load and the decision. The append span parents under the root because it is started while
/// the root is current. Spans carry no exception messages (they can hold SQL or PII), only
/// <c>error.type</c>.
/// </summary>
internal static class CommandTelemetry
{
    internal const string AttemptTag = "alberto.command.attempt";
    private const string LoadName = "alberto.load";

    /// <summary>
    /// Runs a commit under the root span. <paramref name="failure"/> returns the problems of a
    /// rejected result, or null when the result was committed.
    /// </summary>
    internal static async Task<TResult> Run<TResult>(
        string commandType,
        Func<Activity?, Task<TResult>> run,
        Func<TResult, IReadOnlyList<Problem>?> failure)
    {
        using var activity = AlbertoMetrics.Source.StartActivity($"alberto.command {commandType}");
        if (activity is null)
            return await run(null);

        activity.SetTag("alberto.command.type", commandType);
        try
        {
            var result = await run(activity);
            var problems = failure(result);
            activity.SetTag("alberto.command.outcome", problems is null ? "committed" : "rejected");
            TagProblems(activity, problems);
            return result;
        }
        catch (Exception exception)
        {
            // A conflict only escapes Commit once its retries (if any) are used up.
            activity.SetTag("alberto.command.outcome", exception is DcbConflictException ? "conflict" : "error");
            Fail(activity, exception);
            throw;
        }
    }

    internal static async Task<(TState State, DcbBoundary Boundary)> Load<TCommand, TState>(
        Func<TCommand, CancellationToken, Task<(TState State, DcbBoundary Boundary)>> load,
        TCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = Child(LoadName);
        if (activity is null)
            return await load(command, cancellationToken);

        try
        {
            var result = await load(command, cancellationToken);
            activity.SetTag("alberto.load.position", result.Boundary.ExpectedPosition);
            activity.SetTag("alberto.load.types", result.Boundary.Query.Types.Count);
            activity.SetTag("alberto.load.tags", result.Boundary.Query.Tags.Count);
            return result;
        }
        catch (Exception exception)
        {
            Fail(activity, exception);
            throw;
        }
    }

    /// <summary>
    /// Records how many events the load folded, on the load span only: <c>Fold</c> is public
    /// and may be called under somebody else's activity, which must not be tagged.
    /// </summary>
    internal static void EventsRead(int count)
    {
        if (Activity.Current is { OperationName: LoadName } activity)
            activity.SetTag("alberto.load.events", count);
    }

    internal static TResult Decide<TResult>(Func<TResult> decide, Func<TResult, IReadOnlyList<Problem>?> rejection)
    {
        using var activity = Child("alberto.decide");
        if (activity is null)
            return decide();

        try
        {
            var result = decide();
            var problems = rejection(result);
            activity.SetTag("alberto.decide.outcome", problems is null ? "accepted" : "rejected");
            TagProblems(activity, problems);
            return result;
        }
        catch (Exception exception)
        {
            Fail(activity, exception);
            throw;
        }
    }

    private static Activity? Child(string name)
    {
        var activity = AlbertoMetrics.Source.StartActivity(name);
        if (activity?.Parent?.GetTagItem(AttemptTag) is { } attempt)
            activity.SetTag(AttemptTag, attempt);
        return activity;
    }

    private static void TagProblems(Activity activity, IReadOnlyList<Problem>? problems)
    {
        if (problems is { Count: > 0 })
            activity.SetTag("alberto.problem.codes", problems.Select(p => p.Code).Distinct().ToArray());
    }

    private static void Fail(Activity activity, Exception exception)
    {
        activity.SetTag("error.type", exception.GetType().FullName);
        activity.SetStatus(ActivityStatusCode.Error);
    }
}
