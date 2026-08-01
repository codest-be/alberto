namespace Alberto.Subscriptions;

/// <summary>
/// Exception thrown when an optimistic concurrency conflict is detected.
/// This typically happens when two processors try to update the same document concurrently.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>
    /// Creates a new concurrency conflict exception.
    /// </summary>
    /// <param name="documentId">The document ID that had the conflict.</param>
    /// <param name="innerException">The underlying database exception.</param>
    public ConcurrencyConflictException(string documentId, Exception innerException)
        : base($"Concurrency conflict detected for document '{documentId}'.", innerException)
    {
        DocumentId = documentId;
    }

    /// <summary>
    /// The document ID that had the concurrency conflict.
    /// </summary>
    public string DocumentId { get; }
}
