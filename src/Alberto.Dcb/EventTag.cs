using System.Text.RegularExpressions;

namespace Alberto.Dcb;

/// <summary>
/// Represents a tag for an event following the DCB pattern.
/// Tags have the format "concept:id" (e.g., "order:123", "customer:456").
/// Both concept and id accept letters (upper- and lower-case), digits, hyphens, and underscores.
/// </summary>
/// <remarks>
/// Tags intentionally allow mixed case in both <see cref="Concept"/> and <see cref="Id"/> —
/// this differs from <see cref="EventType"/>, whose slugs are restricted to lowercase.
/// </remarks>
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

    // Cached string representations — computed once at construction, never reallocated on access.
    private readonly string _value;

    /// <summary>
    /// The full tag as a string (e.g., "order:123").
    /// </summary>
    public string Value => _value;

    /// <summary>
    /// Constructs a validated <see cref="EventTag"/> from a concept and id.
    /// Use this for tags supplied by application or end-user code.
    /// </summary>
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
        _value = string.Concat(concept, ":", id);
    }

    /// <summary>
    /// Private non-validating constructor. Callers MUST guarantee that concept and id
    /// are already valid (e.g., data read from the database or decomposed from a TagPattern
    /// that was already validated).
    /// </summary>
    private EventTag(string concept, string id, string precomputedValue)
    {
        Concept = concept;
        Id = id;
        _value = precomputedValue;
    }

    /// <summary>
    /// Creates an <see cref="EventTag"/> from data that is already known to be valid,
    /// skipping the validation regex. Use this when constructing tags from storage rows
    /// to avoid redundant regex work on the read hot-path.
    /// </summary>
    /// <param name="concept">The concept component, already validated.</param>
    /// <param name="id">The id component, already validated.</param>
    /// <remarks>
    /// Callers are responsible for ensuring both components satisfy the same constraints
    /// as the public constructor (<c>[a-zA-Z0-9_-]+</c>). Passing invalid values will
    /// produce a structurally inconsistent tag that may cause unexpected behaviour.
    /// </remarks>
    public static EventTag FromStorage(string concept, string id)
        => new(concept, id, string.Concat(concept, ":", id));

    /// <summary>
    /// Creates an <see cref="EventTag"/> from a "concept:id" string that is already known
    /// to be valid, skipping the validation regex. Use this when constructing tags from
    /// storage rows to avoid redundant regex work on the read hot-path.
    /// </summary>
    /// <param name="tag">A string in "concept:id" format, already validated.</param>
    /// <remarks>
    /// Callers are responsible for ensuring the value satisfies the same constraints as
    /// <see cref="Parse"/>. Passing an invalid or mis-formatted value will produce a
    /// structurally inconsistent tag that may cause unexpected behaviour.
    /// </remarks>
    public static EventTag FromStorage(string tag)
    {
        var colonIndex = tag.IndexOf(':');
        // Defensive fallback: if somehow the stored value has no colon, treat the whole
        // string as the concept with an empty-ish id so the struct isn't entirely broken.
        // Production DB rows always have the correct format, but we don't assert here.
        if (colonIndex < 0)
            return new EventTag(tag, string.Empty, tag);

        return new EventTag(
            tag[..colonIndex],
            tag[(colonIndex + 1)..],
            tag);
    }

    /// <summary>
    /// Creates an EventTag from a string in the format "concept:id".
    /// Validates the format and both components. Use for untrusted / user-supplied input.
    /// </summary>
    public static EventTag Parse(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException("Tag cannot be null or empty.", nameof(tag));

        var colonIndex = tag.IndexOf(':');
        if (colonIndex < 0)
            throw new ArgumentException(
                $"Invalid tag format: '{tag}'. Expected format: concept:id",
                nameof(tag));

        return new EventTag(tag[..colonIndex], tag[(colonIndex + 1)..]);
    }

    /// <summary>
    /// Tries to parse a string as an EventTag.
    /// </summary>
    public static bool TryParse(string tag, out EventTag result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var colonIndex = tag.IndexOf(':');
        if (colonIndex < 0)
            return false;

        var concept = tag[..colonIndex];
        var id = tag[(colonIndex + 1)..];

        if (!ValidComponentPattern.IsMatch(concept) || !ValidComponentPattern.IsMatch(id))
            return false;

        // Both components are already validated — use the non-validating constructor.
        result = new EventTag(concept, id, tag);
        return true;
    }

    public bool Equals(EventTag other)
        => string.Equals(Concept, other.Concept, StringComparison.Ordinal)
           && string.Equals(Id, other.Id, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is EventTag other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Concept, Id);

    public static bool operator ==(EventTag left, EventTag right) => left.Equals(right);
    public static bool operator !=(EventTag left, EventTag right) => !left.Equals(right);

    public override string ToString() => _value;

    public static implicit operator string(EventTag tag) => tag._value;
}
