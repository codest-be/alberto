using Alberto.CQRS.Results;

namespace Alberto.CQRS.Queries;

/// <summary>
/// Handler for queries that return a result.
/// </summary>
/// <typeparam name="TQuery">The query type to handle</typeparam>
/// <typeparam name="TResult">The result type</typeparam>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery
{
    /// <summary>
    /// Handles the query execution and returns a result.
    /// </summary>
    /// <param name="query">The query to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A result containing the projected data or errors</returns>
    Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken = default);
}