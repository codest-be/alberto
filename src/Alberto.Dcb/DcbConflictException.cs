namespace Alberto.Dcb;

/// <summary>
/// Exception thrown when a DCB (Dynamic Consistency Boundary) conflict is detected
/// during an append operation. This occurs when events matching the DCB query
/// have been appended after the expected position.
/// </summary>
public sealed class DcbConflictException : Exception
{
    /// <summary>
    /// The <see cref="Problem.Code"/> carried by every conflict surfaced as a <see cref="Problem"/>.
    /// Named so callers branch on a constant rather than a literal they have to keep in step.
    /// </summary>
    public const string ProblemCode = "dcb.conflict";

    /// <summary>
    /// The position where a conflicting event was found.
    /// </summary>
    public long ConflictingPosition { get; }

    /// <summary>
    /// The expected position that was provided to the append operation.
    /// </summary>
    public long ExpectedPosition { get; }

    /// <summary>
    /// The DCB query that was used for the consistency check.
    /// </summary>
    public DcbQuery Query { get; }

    public DcbConflictException(long conflictingPosition, long expectedPosition, DcbQuery query)
        : base($"DCB conflict: event matching query found at position {conflictingPosition} (expected position was {expectedPosition})")
    {
        ConflictingPosition = conflictingPosition;
        ExpectedPosition = expectedPosition;
        Query = query;
    }

    public DcbConflictException(string message, long conflictingPosition, long expectedPosition, DcbQuery query)
        : base(message)
    {
        ConflictingPosition = conflictingPosition;
        ExpectedPosition = expectedPosition;
        Query = query;
    }

    /// <summary>
    /// Creates a DCB conflict exception carrying both the boundary detail and the underlying
    /// provider exception.
    /// </summary>
    /// <remarks>
    /// A backend that learns of a conflict from its database rather than deciding it itself has
    /// two things worth keeping: the provider exception, and the details it can reconstruct from
    /// the append it was performing. This constructor keeps both. Prefer it over
    /// <see cref="DcbConflictException(string, Exception)"/> wherever the details are knowable —
    /// the caller's retry loop reads <see cref="ConflictingPosition"/>, not the message.
    /// </remarks>
    public DcbConflictException(
        string message,
        long conflictingPosition,
        long expectedPosition,
        DcbQuery query,
        Exception innerException)
        : base(message, innerException)
    {
        ConflictingPosition = conflictingPosition;
        ExpectedPosition = expectedPosition;
        Query = query;
    }

    /// <summary>
    /// Creates a DCB conflict exception with a message and inner exception, for the case where
    /// the conflict details genuinely are not available.
    /// </summary>
    /// <remarks>
    /// This constructor reports <see cref="ConflictingPosition"/> and
    /// <see cref="ExpectedPosition"/> as <c>-1</c> and <see cref="Query"/> as
    /// <see cref="DcbQuery.Empty"/>, which renders as <c>*</c>. Those are placeholders, not
    /// facts — a caller cannot distinguish them from a real conflict at position -1 against an
    /// empty query, so anything that inspects the properties silently gets the wrong answer.
    /// Reach for it only when the backend truly cannot say; use
    /// <see cref="DcbConflictException(string, long, long, DcbQuery, Exception)"/> otherwise.
    /// </remarks>
    public DcbConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
        ConflictingPosition = -1;
        ExpectedPosition = -1;
        Query = DcbQuery.Empty;
    }

    /// <summary>
    /// Renders this conflict as a <see cref="Problem"/> carrying <see cref="ProblemCode"/> and both
    /// positions, so a conflict that arrives as an exception and one that arrives as a failed
    /// <see cref="Result"/> are indistinguishable to whatever formats the error.
    /// </summary>
    /// <remarks>
    /// A conflict reaches a caller two ways: as a <see cref="Result"/> failure from a
    /// <c>TryCommit</c>, or as this exception from a <c>Commit</c> whose retries were exhausted.
    /// Both paths go through here, so an error surface only has to handle the one shape.
    /// </remarks>
    public Problem ToProblem() =>
        Problem.Create(
            ProblemCode,
            Message,
            new Dictionary<string, object>
            {
                ["expectedPosition"] = ExpectedPosition,
                ["conflictingPosition"] = ConflictingPosition
            });
}
