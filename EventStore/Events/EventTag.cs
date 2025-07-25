namespace EventStore.Events;

using System.Text.RegularExpressions;

/// <summary>
/// Represents an event tag for a specific instance of a business concept.
/// Format: {concept}:{id} - e.g., "course:123", "student:456"
/// </summary>
public readonly partial struct EventTag : IEquatable<EventTag>
{
    /// <summary>
    /// Regex pattern for valid event tag components (concept and id).
    /// Allows letters, numbers, hyphens, and underscores.
    /// </summary>
    private static readonly Regex ValidComponentPattern = TagRegex();

    [GeneratedRegex("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    /// <summary>
    /// The concept name (e.g., "course", "student")
    /// </summary>
    private string Concept { get; }

    /// <summary>
    /// The instance identifier (e.g., "123", "abc-456")
    /// </summary>
    private string Id { get; }

    /// <summary>
    /// The full event tag as string (e.g., "course:123")
    /// </summary>
    public string FullIdentifier => $"{Concept}:{Id}";

    public EventTag(string concept, string id)
    {
        if (string.IsNullOrWhiteSpace(concept))
            throw new ArgumentException("Concept cannot be null or empty", nameof(concept));

        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id cannot be null or empty", nameof(id));

        if (!ValidComponentPattern.IsMatch(concept))
            throw new ArgumentException(
                $"Concept '{concept}' contains invalid characters. Only letters, numbers, hyphens, and underscores are allowed.",
                nameof(concept));

        if (!ValidComponentPattern.IsMatch(id))
            throw new ArgumentException(
                $"Id '{id}' contains invalid characters. Only letters, numbers, hyphens, and underscores are allowed.",
                nameof(id));

        Concept = concept;
        Id = id;
    }

    /// <summary>
    /// Creates an eventTag from a string in the format "concept:id"
    /// </summary>
    public static EventTag Parse(string eventTag)
    {
        if (string.IsNullOrWhiteSpace(eventTag))
            throw new ArgumentException("Identifier cannot be null or empty", nameof(eventTag));

        var parts = eventTag.Split(':', 2);
        if (parts.Length != 2)
            throw new ArgumentException(
                $"Invalid tag format: {eventTag}. Expected format: concept:id",
                nameof(eventTag));

        return new EventTag(parts[0], parts[1]);
    }

    /// <summary>
    /// Tries to parse a string as an event tag
    /// </summary>
    public static bool TryParse(string tag, out EventTag result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var parts = tag.Split(':', 2);
        if (parts.Length != 2)
            return false;

        if (!ValidComponentPattern.IsMatch(parts[0]) || !ValidComponentPattern.IsMatch(parts[1]))
            return false;

        result = new EventTag(parts[0], parts[1]);
        return true;
    }

    public bool Equals(EventTag other) =>
        string.Equals(Concept, other.Concept) && string.Equals(Id, other.Id);

    public override bool Equals(object? obj) =>
        obj is EventTag other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Concept, Id);

    public static bool operator ==(EventTag left, EventTag right) =>
        left.Equals(right);

    public static bool operator !=(EventTag left, EventTag right) =>
        !left.Equals(right);

    public override string ToString() => FullIdentifier;
}