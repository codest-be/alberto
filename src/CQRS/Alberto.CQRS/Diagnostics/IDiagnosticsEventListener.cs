namespace Alberto.CQRS.Diagnostics;

/// <summary>
/// Listens to diagnostic events for commands and queries.
/// Implement this interface to provide custom telemetry/observability.
/// </summary>
public interface IDiagnosticsEventListener
{
    /// <summary>
    /// Called when a command execution starts.
    /// </summary>
    /// <param name="commandType">The type of the command being executed.</param>
    /// <param name="commandName">The name of the command.</param>
    /// <param name="moduleKey">The module key this command belongs to (null for non-keyed services).</param>
    /// <param name="hasReturnValue">Whether this command returns a result value.</param>
    /// <returns>A scope that provides methods to enrich telemetry and will be disposed when execution completes.</returns>
    ICommandScope Command(Type commandType, string commandName, string? moduleKey, bool hasReturnValue);

    /// <summary>
    /// Called when a query execution starts.
    /// </summary>
    /// <param name="queryType">The type of the query being executed.</param>
    /// <param name="queryName">The name of the query.</param>
    /// <param name="resultType">The type of the result being returned.</param>
    /// <param name="moduleKey">The module key this query belongs to (null for non-keyed services).</param>
    /// <returns>A scope that provides methods to enrich telemetry and will be disposed when execution completes.</returns>
    IQueryScope Query(Type queryType, string queryName, Type resultType, string? moduleKey);
}