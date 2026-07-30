using Alberto.Dcb;

namespace Alberto.Examples.Shared;

/// <summary>
/// Turns a failed <see cref="Result"/> into a GraphQL error, preserving the
/// <see cref="Problem.Code"/> so clients branch on the code rather than the message.
/// </summary>
/// <remarks>
/// Public rather than internal, and shared rather than per-module, because every slice in both
/// modules ends the same way: commit, then surface whatever the decision refused. Twelve copies
/// of this would be twelve chances for one of them to drop the code and leave a client matching
/// on message text.
/// </remarks>
public static class MutationResults
{
    /// <summary>
    /// Throws a <see cref="GraphQLException"/> if the decision refused; otherwise returns.
    /// </summary>
    public static void OrThrow(this Result result)
    {
        if (result.IsFailure)
            throw ToException(result.Problems);
    }

    /// <inheritdoc cref="OrThrow(Result)"/>
    // Fully qualified: HotChocolate's global usings pull in GreenDonut.Result<T>.
    public static T OrThrow<T>(this Alberto.Dcb.Result<T> result) =>
        result.IsSuccess ? result.Value : throw ToException(result.Problems);

    /// <summary>
    /// Awaits a commit and throws a <see cref="GraphQLException"/> if the decision refused, so a
    /// slice ends as one expression: <c>Handle → Load → Decide → Commit → OrThrow</c>.
    /// </summary>
    public static async Task OrThrow(this Task<Result> commit) =>
        (await commit).OrThrow();

    /// <inheritdoc cref="OrThrow(Task{Result})"/>
    public static async Task<T> OrThrow<T>(this Task<Alberto.Dcb.Result<T>> commit) =>
        (await commit).OrThrow();

    private static GraphQLException ToException(IReadOnlyList<Problem> problems) =>
        new(problems
            .Select(problem => ErrorBuilder.New()
                .SetMessage(problem.Message)
                .SetCode(problem.Code)
                .Build())
            .ToArray());
}
