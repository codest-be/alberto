using System.Diagnostics;
using Alberto.Tenancy;

namespace Alberto.Telemetry;

/// <summary>
/// Consume-span attributes and parenting. Error recording is <see cref="BackgroundSpans.Fail"/>.
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

        activity.SetTag("messaging.system", "alberto");
        activity.SetTag("messaging.operation.type", "process");
        activity.SetTag("messaging.consumer.group.name", processorId);
        activity.SetTag("messaging.destination.name", ShardKey.ModuleOf(moduleKey));
        BackgroundSpans.TagProcessor(activity, processorId, moduleKey);
    }

    /// <summary>
    /// Starts a consumer span. With no <paramref name="parent"/> it is a true root: the ambient
    /// activity is cleared first, because <c>StartActivity</c> would otherwise adopt it as the parent
    /// (a sync reactor runs inside the command's append path). The caller restores the ambient
    /// activity once the span is disposed.
    /// </summary>
    internal static Activity? StartConsumer(string name, ActivityContext parent, IEnumerable<ActivityLink>? links)
    {
        if (parent == default)
            Activity.Current = null;
        return AlbertoMetrics.Source.StartActivity(name, ActivityKind.Consumer, parent, links: links);
    }
}
