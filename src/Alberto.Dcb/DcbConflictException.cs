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
}
