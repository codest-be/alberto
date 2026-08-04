namespace Alberto.Testing;

/// <summary>
/// The Then-verbs that read the documents as they stand. They need documents to read but not a
/// baseline to compare against, so they are shared by the stage after <c>Given</c> and the stage
/// after <c>When</c>.
/// </summary>
/// <remarks>
/// <c>ThenDeleted</c> and <c>ThenUnchanged</c> are deliberately not here. Both are claims about a
/// change, and a change needs two points to be measured across, so they live one stage further
/// on — past the <c>When</c> that provides the second point.
/// </remarks>
public abstract class ProjectionAssertions<TState, TSelf> : ProjectionStage<TState, TSelf>
    where TState : new()
    where TSelf : ProjectionAssertions<TState, TSelf>
{
    private protected ProjectionAssertions(ProjectionFold<TState> fold) : base(fold) { }

    /// <summary>
    /// Asserts that exactly one document exists, and asserts on it. The shape most projections
    /// are tested in: one order, one payment, one overview.
    /// </summary>
    public TSelf ThenDocument(Action<TState> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);

        if (Fold.Documents.Count != 1)
            throw new SpecificationException(
                $"Expected exactly one document, but the projection holds {Describe(Fold.Documents)}. " +
                "Name the one you mean with ThenDocument(id, ...).");

        assert(Fold.Documents.Values.Single());
        return Self;
    }

    /// <summary>Asserts that <paramref name="documentId"/> exists, and asserts on it.</summary>
    public TSelf ThenDocument(string documentId, Action<TState> assert)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(assert);

        if (!Fold.Documents.TryGetValue(documentId, out var state))
            throw new SpecificationException(
                $"Expected a document '{documentId}', but the projection holds {Describe(Fold.Documents)}.");

        assert(state);
        return Self;
    }

    /// <summary>
    /// Asserts that <paramref name="documentId"/> does not exist — never written, or written and
    /// since deleted. Use <c>ThenDeleted</c> when the difference is the point.
    /// </summary>
    public TSelf ThenNoDocument(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (Fold.Documents.ContainsKey(documentId))
            throw new SpecificationException(
                $"Expected no document '{documentId}', but the projection holds one.");

        return Self;
    }

    /// <summary>Asserts the projection wrote nothing at all.</summary>
    public TSelf ThenNoDocuments()
    {
        if (Fold.Documents.Count != 0)
            throw new SpecificationException(
                $"Expected the projection to hold nothing, but it holds {Describe(Fold.Documents)}.");

        return Self;
    }

    /// <summary>Asserts how many documents the projection holds.</summary>
    public TSelf ThenDocumentCount(int expected)
    {
        if (Fold.Documents.Count != expected)
            throw new SpecificationException(
                $"Expected {expected} document(s), but the projection holds {Describe(Fold.Documents)}.");

        return Self;
    }

    /// <summary>
    /// The escape hatch: hands you every document, keyed by ID, for assertions the verbs above
    /// do not cover.
    /// </summary>
    public TSelf ThenDocuments(Action<IReadOnlyDictionary<string, TState>> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(Fold.Documents);
        return Self;
    }

    private protected static string Describe(IReadOnlyDictionary<string, TState> documents) =>
        documents.Count == 0
            ? "none"
            : $"{documents.Count}: {Names(documents.Keys)}";

    private protected static string Names(IEnumerable<string> ids)
    {
        var listed = string.Join(", ", ids.Order(StringComparer.Ordinal).Select(id => $"'{id}'"));
        return listed.Length == 0 ? "nothing" : listed;
    }
}
