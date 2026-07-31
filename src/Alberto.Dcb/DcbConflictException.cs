namespace Alberto.Dcb;

/// <summary>
/// Exception thrown when a DCB (Dynamic Consistency Boundary) conflict is detected
/// during an append operation. This occurs when events matching the DCB query
/// have been appended after the expected position.
/// </summary>
public sealed class DcbConflictException : Exception
{
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
}
