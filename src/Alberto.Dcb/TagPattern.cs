using System.Text.RegularExpressions;

namespace Alberto.Dcb;

/// <summary>
/// Represents a tag query pattern that supports exact matching or prefix wildcards.
/// </summary>
/// <remarks>
/// Patterns can be:
/// <list type="bullet">
///   <item><description>Exact: "order:123" matches only that specific tag</description></item>
///   <item><description>Prefix wildcard: "order:*" matches all tags starting with "order:"</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Exact match
/// var exact = TagPattern.Exact("order", "123");
/// var exact2 = TagPattern.Parse("order:123");
///
/// // Prefix wildcard - matches order:1, order:abc, order:xyz, etc.
/// var wildcard = TagPattern.Prefix("order");
/// var wildcard2 = TagPattern.Parse("order:*");
///
/// // Check if a tag matches
/// var tag = new EventTag("order", "456");
/// wildcard.Matches(tag); // true
/// exact.Matches(tag);    // false
/// </code>
/// </example>
public readonly partial struct TagPattern : IEquatable<TagPattern>
{
    private static readonly Regex ValidConceptPattern = ConceptRegex();

    [GeneratedRegex("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled)]
    private static partial Regex ConceptRegex();

    /// <summary>
    /// The concept/category part of the tag (e.g., "order", "customer").
    /// </summary>
    public string Concept { get; }

    /// <summary>
    /// The specific ID to match, or null for wildcard patterns.
    /// </summary>
    public string? Id { get; }

    /// <summary>
    /// Returns true if this is a wildcard pattern (matches any ID for the concept).
    /// </summary>
    public bool IsWildcard => Id is null;

    /// <summary>
    /// Returns true if this is an exact match pattern.
    /// </summary>
    public bool IsExact => Id is not null;

    // Cached string representations — computed once at construction, never reallocated on access.
    private readonly string _value;
    private readonly string _conceptPrefix;

    /// <summary>
    /// Gets the pattern as a string for display/debugging.
    /// </summary>
    public string Value => _value;

    /// <summary>
    /// Gets the concept prefix for LIKE queries (e.g., "order:" for wildcard patterns).
    /// </summary>
    public string ConceptPrefix => _conceptPrefix;

    private TagPattern(string concept, string? id)
    {
        Concept = concept;
        Id = id;
        _conceptPrefix = string.Concat(concept, ":");
        _value = id is null ? string.Concat(concept, ":*") : string.Concat(concept, ":", id);
    }

    /// <summary>
    /// Creates an exact match pattern for a specific tag.
    /// </summary>
    public static TagPattern Exact(string concept, string id)
    {
        ValidateConcept(concept);
        ValidateId(id);
        return new TagPattern(concept, id);
    }

    /// <summary>
    /// Creates an exact match pattern from an EventTag.
    /// </summary>
    public static TagPattern Exact(EventTag tag)
        => new(tag.Concept, tag.Id);

    /// <summary>
    /// Creates a prefix wildcard pattern that matches all tags with the given concept.
    /// </summary>
    public static TagPattern Prefix(string concept)
    {
        ValidateConcept(concept);
        return new TagPattern(concept, null);
    }

    /// <summary>
    /// Parses a pattern string. Supports "concept:id" for exact match or "concept:*" for wildcards.
    /// </summary>
    public static TagPattern Parse(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern cannot be null or empty.", nameof(pattern));

        var colonIndex = pattern.IndexOf(':');
        if (colonIndex < 0)
            throw new ArgumentException(
                $"Invalid pattern format: '{pattern}'. Expected format: concept:id or concept:*",
                nameof(pattern));

        var concept = pattern[..colonIndex];
        var id = pattern[(colonIndex + 1)..];

        if (id == "*")
            return Prefix(concept);

        return Exact(concept, id);
    }

    /// <summary>
    /// Tries to parse a pattern string.
    /// </summary>
    public static bool TryParse(string pattern, out TagPattern result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var colonIndex = pattern.IndexOf(':');
        if (colonIndex < 0)
            return false;

        var concept = pattern[..colonIndex];
        var id = pattern[(colonIndex + 1)..];

        if (!ValidConceptPattern.IsMatch(concept))
            return false;

        if (id == "*")
        {
            result = new TagPattern(concept, null);
            return true;
        }

        if (!ValidConceptPattern.IsMatch(id))
            return false;

        result = new TagPattern(concept, id);
        return true;
    }

    /// <summary>
    /// Checks if the given tag matches this pattern.
    /// </summary>
    public bool Matches(EventTag tag)
    {
        if (!string.Equals(Concept, tag.Concept, StringComparison.Ordinal))
            return false;

        return IsWildcard || string.Equals(Id, tag.Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if the given tag value string matches this pattern.
    /// Uses cached string values — no per-call allocation.
    /// </summary>
    public bool Matches(string tagValue)
    {
        if (IsWildcard)
            return tagValue.StartsWith(_conceptPrefix, StringComparison.Ordinal);

        return string.Equals(tagValue, _value, StringComparison.Ordinal);
    }

    private static void ValidateConcept(string concept)
    {
        if (string.IsNullOrWhiteSpace(concept))
            throw new ArgumentException("Concept cannot be null or empty.", nameof(concept));

        if (!ValidConceptPattern.IsMatch(concept))
            throw new ArgumentException(
                $"Concept '{concept}' contains invalid characters. Only letters, numbers, hyphens, and underscores are allowed.",
                nameof(concept));
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id cannot be null or empty.", nameof(id));

        if (!ValidConceptPattern.IsMatch(id))
            throw new ArgumentException(
                $"Id '{id}' contains invalid characters. Only letters, numbers, hyphens, and underscores are allowed.",
                nameof(id));
    }

    public bool Equals(TagPattern other)
        => string.Equals(Concept, other.Concept, StringComparison.Ordinal)
           && string.Equals(Id, other.Id, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TagPattern other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Concept, Id);

    public static bool operator ==(TagPattern left, TagPattern right) => left.Equals(right);
    public static bool operator !=(TagPattern left, TagPattern right) => !left.Equals(right);

    public override string ToString() => _value;

    public static implicit operator string(TagPattern pattern) => pattern._value;

    /// <summary>
    /// Implicit conversion from EventTag creates an exact match pattern.
    /// </summary>
    public static implicit operator TagPattern(EventTag tag) => Exact(tag);
}
