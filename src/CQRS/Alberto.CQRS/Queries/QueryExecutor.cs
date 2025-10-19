using Alberto.CQRS.Diagnostics;
using Alberto.CQRS.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Queries;

/// <summary>
/// Executes queries.
/// </summary>
public sealed class QueryExecutor(IServiceProvider serviceProvider, string? moduleKey = null)
{
    private readonly IDiagnosticsEventListener? _diagnostics =
        serviceProvider.GetService<IDiagnosticsEventListener>();

    /// <summary>
    /// Executes a query and returns a result.
    /// </summary>
    public async Task<Result<TResult>> Execute<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken = default)
        where TQuery : IQuery
    {
        using var scope = _diagnostics?.Query(typeof(TQuery), typeof(TQuery).Name, typeof(TResult), moduleKey)
                          ?? EmptyDisposable.Instance;

        // Get and execute handler
        var handler = GetHandler<IQueryHandler<TQuery, TResult>>();
        if (handler == null)
        {
            var error = $"No handler found for query {typeof(TQuery).Name}";
            scope.WithError(error);
            return Result<TResult>.Fail(error);
        }

        scope.WithHandler(handler.GetType());

        var result = await handler.Handle(query, cancellationToken);

        if (result.IsSuccess)
            scope.WithOutcome("success");
        else
            scope.WithError("Query execution failed", result.Problems.Select(p => p.Code));

        return result;
    }

    private TService? GetHandler<TService>() where TService : class
    {
        return moduleKey != null
            ? serviceProvider.GetKeyedService<TService>(moduleKey)
            : serviceProvider.GetService<TService>();
    }
}