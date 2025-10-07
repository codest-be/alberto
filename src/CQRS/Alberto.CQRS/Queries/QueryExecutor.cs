using Alberto.CQRS.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Queries;

/// <summary>
/// Executes queries.
/// </summary>
public sealed class QueryExecutor(IServiceProvider serviceProvider, string? moduleKey = null)
{
    /// <summary>
    /// Executes a query and returns a result.
    /// </summary>
    public async Task<Result<TResult>> Execute<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken = default)
        where TQuery : IQuery
    {
        // Get and execute handler
        var handler = GetHandler<IQueryHandler<TQuery, TResult>>();
        if (handler == null)
            return Result<TResult>.Fail($"No handler found for query {typeof(TQuery).Name}");

        return await handler.Handle(query, cancellationToken);
    }

    private TService? GetHandler<TService>() where TService : class
    {
        return moduleKey != null
            ? serviceProvider.GetKeyedService<TService>(moduleKey)
            : serviceProvider.GetService<TService>();
    }
}