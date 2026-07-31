using Alberto;

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
    public static T OrThrow<T>(this Alberto.Result<T> result) =>
        result.IsSuccess ? result.Value : throw ToException(result.Problems);

    /// <summary>
    /// Awaits a commit and throws a <see cref="GraphQLException"/> if the decision refused, so a
    /// slice ends as one expression: <c>Handle → Load → Decide → Commit → OrThrow</c>.
    /// </summary>
    /// <remarks>
    /// A refusal is not the only way a commit fails. <c>Commit</c> retries a
    /// <see cref="DcbConflictException"/> up to its attempt limit and then rethrows, so a boundary
    /// that stays contended outlives the retries and arrives here as an exception rather than a
    /// failed <see cref="Result"/>. Catching it is what lets a slice keep this shape instead of
    /// switching to <c>TryCommit</c> purely to get a coded error: the client sees
    /// <see cref="DcbConflictException.ProblemCode"/> either way, and a retry is worth its while.
    /// Left uncaught it would surface as an unhandled exception — a 500 with no code on it.
    /// </remarks>
    public static async Task OrThrow(this Task<Result> commit)
    {
        try
        {
            (await commit).OrThrow();
        }
        catch (DcbConflictException conflict)
        {
            throw ToException([conflict.ToProblem()]);
        }
    }

    /// <inheritdoc cref="OrThrow(Task{Result})"/>
    public static async Task<T> OrThrow<T>(this Task<Alberto.Result<T>> commit)
    {
        try
        {
            return (await commit).OrThrow();
        }
        catch (DcbConflictException conflict)
        {
            throw ToException([conflict.ToProblem()]);
        }
    }

    // Details become extensions rather than being dropped: a conflict carries the two positions
    // a client needs to decide whether retrying is worth its while, and no other channel would
    // survive the trip to GraphQL.
    private static GraphQLException ToException(IReadOnlyList<Problem> problems) =>
        new(problems
            .Select(problem =>
            {
                var error = ErrorBuilder.New()
                    .SetMessage(problem.Message)
                    .SetCode(problem.Code);

                foreach (var (key, value) in problem.Details)
                    error.SetExtension(key, value);

                return error.Build();
            })
            .ToArray());
}
