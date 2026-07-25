namespace Alberto.Dcb;

/// <summary>
/// A DCB consistency boundary: the query that scoped the read, and the position it was read at.
/// Both halves are needed for the conflict check on append, which is why they travel together.
/// </summary>
internal readonly record struct DcbBoundary(DcbQuery Query, long ExpectedPosition);

/// <summary>
/// A command on its way through the pipeline — either the current value, or the problems
/// that stopped it. Failures short-circuit by carrying problems here rather than by
/// nulling the command out.
/// </summary>
internal readonly record struct Staged<TCommand>(TCommand Command, IReadOnlyList<Problem>? Problems)
{
    public bool HasProblems => Problems is { Count: > 0 };

    public static Staged<TCommand> Ok(TCommand command) => new(command, null);

    public static Staged<TCommand> Failed(IReadOnlyList<Problem> problems) => new(default!, problems);
}

/// <summary>
/// Runs an async factory at most once. This is what makes <c>RetryOnConflict</c> safe:
/// a retry re-runs <c>Load</c> and <c>Decide</c>, but never the enrichment that produced
/// the command — enrichment may have had side effects that must not repeat.
/// </summary>
/// <remarks>Not thread-safe. A pipeline is a single-use, single-threaded value.</remarks>
internal sealed class Once<T>(Func<CancellationToken, Task<T>> factory)
{
    private Task<T>? _task;

    public Task<T> Get(CancellationToken cancellationToken) => _task ??= factory(cancellationToken);
}

internal static class PipelineInternals
{
    /// <summary>
    /// The pipeline stages are structs, so <c>default(...)</c> produces one with no store.
    /// Fail with a message that names the cause instead of a bare <see cref="NullReferenceException"/>.
    /// </summary>
    internal static AlbertoStore Require(AlbertoStore? store) =>
        store ?? throw new InvalidOperationException(
            "This pipeline stage was obtained from default(...) rather than from store.Handle(command). " +
            "Every pipeline must start at AlbertoStore.Handle.");

    /// <summary>The problem a <c>TryCommit</c> returns in place of a thrown conflict.</summary>
    internal static Problem Conflict(DcbConflictException exception) =>
        Problem.Create(
            "dcb.conflict",
            exception.Message,
            new Dictionary<string, object>
            {
                ["expectedPosition"] = exception.ExpectedPosition,
                ["conflictingPosition"] = exception.ConflictingPosition
            });
}
