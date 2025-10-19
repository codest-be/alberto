using System.Diagnostics;
using Alberto.CQRS.Diagnostics;

namespace Alberto.CQRS.Telemetry.Scopes;

internal sealed class QueryScope(Activity activity) : IQueryScope
{
    public const string ActivityName = "Alberto.CQRS.Query";
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        activity.Dispose();
        _disposed = true;
    }

    public IQueryScope WithHandler(Type handlerType)
    {
        activity.SetTag(Tags.QueryHandlerType, handlerType.FullName ?? handlerType.Name);
        return this;
    }

    public IQueryScope WithOutcome(string outcome)
    {
        activity.SetTag(Tags.Outcome, outcome);
        return this;
    }

    public IQueryScope WithError(string errorMessage, IEnumerable<string>? problemCodes = null)
    {
        activity.SetTag(Tags.Outcome, "failure");
        activity.SetTag(Tags.ErrorMessage, errorMessage);

        if (problemCodes != null)
            activity.SetTag(Tags.ProblemCodes, string.Join(", ", problemCodes));

        return this;
    }

    public QueryScope WithQueryInfo(Type queryType, string queryName, Type resultType, string? moduleKey)
    {
        activity.DisplayName = $"Query: {queryName}";
        activity.SetTag(Tags.QueryType, queryType.FullName ?? queryType.Name);
        activity.SetTag(Tags.QueryName, queryName);
        activity.SetTag(Tags.QueryResultType, resultType.FullName ?? resultType.Name);

        if (!string.IsNullOrEmpty(moduleKey))
            activity.SetTag(Tags.ModuleKey, moduleKey);

        return this;
    }
}