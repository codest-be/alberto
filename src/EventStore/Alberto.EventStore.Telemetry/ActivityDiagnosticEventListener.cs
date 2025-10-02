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
}