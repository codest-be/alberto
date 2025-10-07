using Alberto.EventSourcing;
using Microsoft.AspNetCore.Http;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Alberto.CQRS.Results;

/// <summary>
/// Extension methods to convert Result types to ASP.NET Core IResult responses.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Converts a Result with a value to an HTTP response.
    /// Success returns 200 OK, failure returns Problem Details.
    /// </summary>
    public static IResult ToHttpResult<T>(this Result<T> result)
    {
        if (result.IsSuccess)
            return HttpResults.Ok(result.Value);

        return CreateProblemResult(result.Problems);
    }

    /// <summary>
    /// Converts a Result without a value to an HTTP response.
    /// Success returns 204 No Content, failure returns Problem Details.
    /// </summary>
    public static IResult ToHttpResult(this Result result)
    {
        if (result.IsSuccess)
            return HttpResults.NoContent();

        return CreateProblemResult(result.Problems);
    }

    /// <summary>
    /// Converts a Result with a value to an HTTP response with custom success status code.
    /// </summary>
    public static IResult ToHttpResult<T>(this Result<T> result, int successStatusCode)
    {
        if (result.IsSuccess)
            return HttpResults.Json(result.Value, statusCode: successStatusCode);

        return CreateProblemResult(result.Problems);
    }

    /// <summary>
    /// Converts a Result with a value to a Created response (201).
    /// </summary>
    public static IResult ToCreatedResult<T>(this Result<T> result, string uri)
    {
        if (result.IsSuccess)
            return HttpResults.Created(uri, result.Value);

        return CreateProblemResult(result.Problems);
    }

    /// <summary>
    /// Converts a Result with a value to an Accepted response (202).
    /// </summary>
    public static IResult ToAcceptedResult<T>(this Result<T> result, string? uri = null)
    {
        if (result.IsSuccess)
            return HttpResults.Accepted(uri, result.Value);

        return CreateProblemResult(result.Problems);
    }

    private static IResult CreateProblemResult(IReadOnlyList<Problem> problems)
    {
        var statusCode = MapStatusCode(problems);
        var firstProblem = problems.First();

        return HttpResults.Problem(
            statusCode: statusCode,
            title: firstProblem.Code,
            detail: problems.Count == 1
                ? firstProblem.Message
                : string.Join("; ", problems.Select(p => p.Message)),
            extensions: new Dictionary<string, object?>
            {
                ["errors"] = problems.Select(p => new
                {
                    code = p.Code, message = p.Message, details = p.Details.Count > 0 ? p.Details : null
                }).ToArray()
            });
    }

    private static int MapStatusCode(IReadOnlyList<Problem> problems)
    {
        // Map error codes to HTTP status codes based on common patterns
        var firstCode = problems.First().Code.ToUpperInvariant();

        return firstCode switch
        {
            var c when c.Contains("NOT_FOUND") || c.Contains("NOTFOUND") => StatusCodes.Status404NotFound,
            var c when c.Contains("UNAUTHORIZED") => StatusCodes.Status401Unauthorized,
            var c when c.Contains("FORBIDDEN") => StatusCodes.Status403Forbidden,
            var c when c.Contains("CONFLICT") || c.Contains("ALREADY_EXISTS") => StatusCodes.Status409Conflict,
            var c when c.Contains("VALIDATION") || c.Contains("INVALID") => StatusCodes.Status400BadRequest,
            var c when c.Contains("UNPROCESSABLE") => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest
        };
    }
}