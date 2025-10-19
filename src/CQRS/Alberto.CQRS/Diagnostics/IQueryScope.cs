namespace Alberto.CQRS.Diagnostics;

/// <summary>
/// Represents a scope for query execution telemetry.
/// Provides methods to enrich telemetry with execution details.
/// </summary>
public interface IQueryScope : IDisposable
{
    /// <summary>
    /// Records the handler type that processed this query.
    /// </summary>
    IQueryScope WithHandler(Type handlerType);

    /// <summary>
    /// Records a successful outcome.
    /// </summary>
    IQueryScope WithOutcome(string outcome);

    /// <summary>
    /// Records an error with optional problem codes.
    /// </summary>
    IQueryScope WithError(string errorMessage, IEnumerable<string>? problemCodes = null);
}