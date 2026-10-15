using System.Diagnostics;
using Alberto.Tenancy;

namespace Alberto.Telemetry;

/// <summary>
/// Span attributes and error recording shared by the consume middlewares and the append interceptor.
/// </summary>
internal static class TelemetrySpans
{
    /// <summary>
    /// The messaging attributes of a consume span, plus Alberto's own <c>alberto.*</c> ones.
    /// Alberto's attributes are namespaced; metric tag keys are a separate contract and are not.
    /// </summary>
    internal static void TagConsumer(Activity? activity, string processorId, string moduleKey)
    {
        if (activity is null)
            return;

        var module = ShardKey.ModuleOf(moduleKey);
        activity.SetTag("messaging.system", "alberto");
        activity.SetTag("messaging.operation.type", "process");
        activity.SetTag("messaging.consumer.group.name", processorId);
        activity.SetTag("messaging.destination.name", module);
        activity.SetTag("alberto.processor.id", processorId);
        activity.SetTag("alberto.module", module);
        if (ShardKey.ShardOf(moduleKey) is { } shardId)
            activity.SetTag("alberto.shard", shardId);
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
}
