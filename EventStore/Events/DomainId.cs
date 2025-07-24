namespace EventStore.Events;

/// <summary>
/// Represents a domain identifier for a specific instance of a business concept.
/// Format: {concept}:{id} - e.g., "course:123", "student:456"
/// </summary>
public readonly struct DomainId : IEquatable<DomainId>
{
    /// <summary>
    /// The concept name (e.g., "course", "student")
    /// </summary>
    private string Concept { get; }

    /// <summary>
    /// The instance identifier (e.g., "123", "abc-456")
    /// </summary>
    private string Id { get; }

    /// <summary>
    /// The full domain identifier as string (e.g., "course:123")
    /// </summary>
    public string FullIdentifier => $"{Concept}:{Id}";

    public DomainId(string concept, string id)
    {
        if (string.IsNullOrWhiteSpace(concept))
            throw new ArgumentException("Concept cannot be null or empty", nameof(concept));

        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id cannot be null or empty", nameof(id));

        Concept = concept;
        Id = id;
    }

    /// <summary>
    /// Creates a domain identifier from a string in the format "concept:id"
    /// </summary>
    public static DomainId Parse(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier cannot be null or empty", nameof(identifier));

        var parts = identifier.Split(':', 2);
        if (parts.Length != 2)
            throw new ArgumentException(
                $"Invalid domain identifier format: {identifier}. Expected format: concept:id",
                nameof(identifier));

        return new DomainId(parts[0], parts[1]);
    }

    /// <summary>
    /// Tries to parse a string as a domain identifier
    /// </summary>
    public static bool TryParse(string identifier, out DomainId result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        var parts = identifier.Split(':', 2);
        if (parts.Length != 2)
            return false;

        result = new DomainId(parts[0], parts[1]);
        return true;
    }

    public bool Equals(DomainId other) =>
        string.Equals(Concept, other.Concept) && string.Equals(Id, other.Id);

    public override bool Equals(object? obj) =>
        obj is DomainId other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Concept, Id);

    public static bool operator ==(DomainId left, DomainId right) =>
        left.Equals(right);

    public static bool operator !=(DomainId left, DomainId right) =>
        !left.Equals(right);

    public override string ToString() => FullIdentifier;
}