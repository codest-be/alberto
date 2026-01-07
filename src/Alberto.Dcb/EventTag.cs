using System.Text.RegularExpressions;

namespace Alberto.Dcb;

/// <summary>
/// Represents a tag for an event following the DCB pattern.
/// Tags have the format "concept:id" (e.g., "order:123", "customer:456").
/// </summary>
public readonly partial struct EventTag : IEquatable<EventTag>
{
    private static readonly Regex ValidComponentPattern = TagComponentRegex();

    [GeneratedRegex("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled)]
    private static partial Regex TagComponentRegex();

    /// <summary>
    /// The concept name (e.g., "order", "customer", "venue").
    /// </summary>
    public string Concept { get; }

    /// <summary>
    /// The instance identifier (e.g., "123", "abc-456").
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The full tag as a string (e.g., "order:123").
    /// </summary>
    public string Value => $"{Concept}:{Id}";

    public EventTag(string concept, string id)
    {
        if (string.IsNullOrWhiteSpace(concept))
            throw new ArgumentException("Concept cannot be null or empty.", nameof(concept));

        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id cannot be null or empty.", nameof(id));

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
    /// Creates an EventTag from a string in the format "concept:id".
    /// </summary>
    public static EventTag Parse(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException("Tag cannot be null or empty.", nameof(tag));

        var parts = tag.Split(':', 2);
        if (parts.Length != 2)
            throw new ArgumentException(
                $"Invalid tag format: '{tag}'. Expected format: concept:id",
                nameof(tag));

        return new EventTag(parts[0], parts[1]);
    }

    /// <summary>
    /// Tries to parse a string as an EventTag.
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

    public bool Equals(EventTag other)
        => string.Equals(Concept, other.Concept, StringComparison.Ordinal)
           && string.Equals(Id, other.Id, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is EventTag other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Concept, Id);

    public static bool operator ==(EventTag left, EventTag right) => left.Equals(right);
    public static bool operator !=(EventTag left, EventTag right) => !left.Equals(right);

    public override string ToString() => Value;

    public static implicit operator string(EventTag tag) => tag.Value;
}
