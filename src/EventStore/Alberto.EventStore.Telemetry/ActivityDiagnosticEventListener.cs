using System.Diagnostics;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.Telemetry.Scopes;

namespace Alberto.EventStore.Telemetry;

internal class ActivityDiagnosticEventListener : IDiagnosticsEventListener
{
    public IDisposable Stream(StreamQuery query, int? maxCount)
    {
        Activity? activity =
            AlbertoActivitySource.Source.CreateActivity(StreamScope.ActivityName, ActivityKind.Internal);

        if (activity is null)
            return new EmptyScope();

        activity.Start();

        return new StreamScope(activity).WithQuery(query, maxCount);
    }

    public IDisposable Append(IEventToPersist[] events)
    {
        Activity? activity =
            AlbertoActivitySource.Source.CreateActivity(AppendScope.ActivityName, ActivityKind.Internal);

        if (activity is null)
            return new EmptyScope();

        activity.Start();

        return new AppendScope(activity).WithEvents(events);
    }

    public Dictionary<string, string> GetTelemetryMetadata()
    {
        var currentActivity = Activity.Current;
        if (currentActivity == null)
            return new Dictionary<string, string>();

        // Walk up to find the root activity in the chain to get the HTTP request's trace ID
        var rootActivity = currentActivity;
        while (rootActivity.Parent != null)
        {
            rootActivity = rootActivity.Parent;
        }

        return new Dictionary<string, string> { ["_traceId"] = rootActivity.TraceId.ToString(), ["_spanId"] = rootActivity.SpanId.ToString() };
    }
}