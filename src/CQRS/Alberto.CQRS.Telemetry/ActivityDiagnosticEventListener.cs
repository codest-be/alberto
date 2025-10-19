using System.Diagnostics;
using Alberto.CQRS.Diagnostics;
using Alberto.CQRS.Telemetry.Scopes;

namespace Alberto.CQRS.Telemetry;

internal class ActivityDiagnosticEventListener : IDiagnosticsEventListener
{
    public ICommandScope Command(Type commandType, string commandName, string? moduleKey, bool hasReturnValue)
    {
        Activity? activity =
            AlbertoCQRSActivitySource.Source.CreateActivity(CommandScope.ActivityName, ActivityKind.Internal);

        if (activity is null)
            return EmptyScope.Instance;

        activity.Start();

        return new CommandScope(activity).WithCommandInfo(commandType, commandName, moduleKey, hasReturnValue);
    }

    public IQueryScope Query(Type queryType, string queryName, Type resultType, string? moduleKey)
    {
        Activity? activity =
            AlbertoCQRSActivitySource.Source.CreateActivity(QueryScope.ActivityName, ActivityKind.Internal);

        if (activity is null)
            return EmptyScope.Instance;

        activity.Start();

        return new QueryScope(activity).WithQueryInfo(queryType, queryName, resultType, moduleKey);
    }
}