using System.Diagnostics;

namespace Alberto.Telemetry;

/// <summary>
/// Spans for work Alberto does on its own schedule (polls, checkpoint saves, leases, rebuilds,
/// outbox retention), plus the error recording every Alberto span shares.
/// </summary>
/// <remarks>
/// A background span is a root. A loop started from inside some other span (a test's, or a host
/// that starts hosted services under one) would otherwise parent every poll for the life of the
/// process to it. <see cref="Activity.Current"/> is an <c>AsyncLocal</c>, so clearing it inside the
/// async method that owns the loop is invisible to that method's caller.
/// </remarks>
internal static class BackgroundSpans
{
    /// <summary>
    /// Starts a root span that becomes <see cref="Activity.Current"/>, so child spans (the read under
    /// a poll) nest under it. Call only from the async method that owns the span: that method's
    /// caller keeps its own ambient activity because the change does not flow back out.
    /// </summary>
    internal static Activity? StartRoot(string name, ActivityKind kind = ActivityKind.Internal)
    {
        if (!AlbertoMetrics.Source.HasListeners())
            return null;

        var ambient = Activity.Current;
        Activity.Current = null;
        var activity = AlbertoMetrics.Source.StartActivity(name, kind, parentContext: default);
        if (activity is null)
            Activity.Current = ambient;
        return activity;
    }

    /// <summary>
    /// Starts a root span that is never <see cref="Activity.Current"/>: the ambient activity is
    /// restored as soon as it starts. For a span that outlives the call that opens it (one per
    /// rebuild), which must not parent whatever that call's caller does next.
    /// </summary>
    internal static Activity? StartDetached(string name)
    {
        var ambient = Activity.Current;
        var activity = StartRoot(name);
        Activity.Current = ambient;
        return activity;
    }

    /// <summary>Ends a span opened with <see cref="StartDetached"/> without disturbing the ambient activity.</summary>
    internal static void StopDetached(Activity? activity)
    {
        if (activity is null)
            return;

        var ambient = Activity.Current;
        activity.Dispose();
        Activity.Current = ambient;
    }

    /// <summary>
    /// Marks the span failed without the exception's message, which can carry SQL or PII (Npgsql's
    /// do): <c>error.type</c> and an <c>exception</c> event holding only the type. Status stays
    /// description-less, and successful spans stay <c>Unset</c>.
    /// </summary>
    internal static void Fail(Activity? activity, Exception? exception)
    {
        if (activity is null)
            return;

        var type = exception?.GetType().FullName ?? "dead_lettered";
        activity.SetTag("error.type", type);
        activity.SetStatus(ActivityStatusCode.Error);
        if (exception is not null)
            activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { { "exception.type", type } }));
    }

    /// <summary>The <c>alberto.processor.id</c>/<c>alberto.module</c>/<c>alberto.shard</c> attributes.</summary>
    internal static void TagProcessor(Activity? activity, string processorId, string moduleKey)
    {
        if (activity is null)
            return;

        activity.SetTag("alberto.processor.id", processorId);
        TagModule(activity, moduleKey);
    }

    internal static void TagModule(Activity? activity, string moduleKey)
    {
        if (activity is null)
            return;

        activity.SetTag("alberto.module", Tenancy.ShardKey.ModuleOf(moduleKey));
        if (Tenancy.ShardKey.ShardOf(moduleKey) is { } shardId)
            activity.SetTag("alberto.shard", shardId);
    }

    /// <summary>
    /// The log scope a background loop runs under, keyed like the span attributes so a log line and
    /// the span around it filter on the same names.
    /// </summary>
    internal static Dictionary<string, object?> Scope(string? processorId, string moduleKey, string? tenantId = null)
    {
        var scope = new Dictionary<string, object?> { ["alberto.module"] = Tenancy.ShardKey.ModuleOf(moduleKey) };
        if (processorId is not null)
            scope["alberto.processor.id"] = processorId;
        if (Tenancy.ShardKey.ShardOf(moduleKey) is { } shardId)
            scope["alberto.shard"] = shardId;
        if (tenantId is not null)
            scope["alberto.tenant.id"] = tenantId;
        return scope;
    }
}
