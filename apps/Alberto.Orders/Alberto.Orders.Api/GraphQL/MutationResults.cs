using Alberto.Dcb;

namespace Alberto.Orders.Api.GraphQL;

/// <summary>
/// Turns a failed <see cref="Result"/> into a GraphQL error, preserving the
/// <see cref="Problem.Code"/> so clients branch on the code rather than the message.
/// </summary>
internal static class MutationResults
{
    public static void EnsureCommitted(this Result result)
    {
        if (result.IsFailure)
            throw ToException(result.Problems);
    }

    // Fully qualified: HotChocolate's global usings pull in GreenDonut.Result<T>.
    public static T EnsureCommitted<T>(this Alberto.Dcb.Result<T> result) =>
        result.IsSuccess ? result.Value : throw ToException(result.Problems);

    private static GraphQLException ToException(IReadOnlyList<Problem> problems) =>
        new(problems
            .Select(problem => ErrorBuilder.New()
                .SetMessage(problem.Message)
                .SetCode(problem.Code)
                .Build())
            .ToArray());
}
