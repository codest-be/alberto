using System.Diagnostics;
using EventStore.Diagnostics;
using EventStore.Events;
using EventStore.Telemetry.Scopes;

namespace EventStore.Telemetry;

internal class ActivityDiagnosticEventListener : IDiagnosticsEventListener
{
    public IDisposable Stream(StreamQuery query, int? maxCount)
    {
        var activity = AlbertoActivitySource.Source.CreateActivity(StreamScope.ActivityName, ActivityKind.Internal);

        if (activity is null)
            return new EmptyScope();

        activity.Start();

        return new StreamScope(activity).WithQuery(query, maxCount);
    }

    public IDisposable Append(IEventToPersist[] events)
    {
        var activity = AlbertoActivitySource.Source.CreateActivity(AppendScope.ActivityName, ActivityKind.Internal);

        if (activity is null)
            return new EmptyScope();

        activity.Start();

        return new AppendScope(activity).WithEvents(events);
    }
}