using System.Text.RegularExpressions;

namespace Alberto;

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
    /// <summary>
    /// The prefix reserved for framework-internal tag concepts. Application code MUST NOT create
    /// a tag or a <see cref="TagAttribute"/> whose concept starts with this prefix — doing so
    /// throws <see cref="ArgumentException"/>.
    /// </summary>
    /// <remarks>
    /// The reservation covers the whole prefix rather than the individual concept names Alberto
    /// happens to use today. Domain concepts are things like "order", "customer", or "venue", so
    /// nothing is lost, and any framework tag added later lands in space that was never available
    /// to applications. Reserving only the names in use would make every future framework tag a
    /// breaking change for whoever had already chosen that name.
    /// </remarks>
    public const string ReservedConceptPrefix = "_";

    /// <summary>
    /// The reserved tag concept used to carry the event schema version on every persisted event.
    /// Stored tags read <c>_version:2</c>.
    /// </summary>
    /// <remarks>
    /// Covered by <see cref="ReservedConceptPrefix"/>, so application code cannot author it.
    /// </remarks>
    public const string SchemaVersionConcept = "_version";

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
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="concept"/> starts with <see cref="ReservedConceptPrefix"/> —
    /// that space is reserved for framework-internal use and must not be used by application code.
    /// </exception>
    public EventTag(string concept, string id)
    {
        if (string.IsNullOrWhiteSpace(concept))
            throw new ArgumentException("Concept cannot be null or empty.", nameof(concept));

        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id cannot be null or empty.", nameof(id));

        if (IsReservedConcept(concept))
            throw new ArgumentException(ReservedConceptMessage(concept), nameof(concept));

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
    /// True when <paramref name="concept"/> falls in the framework-reserved namespace.
    /// </summary>
    internal static bool IsReservedConcept(string concept)
        => concept.StartsWith(ReservedConceptPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The rejection message shared by every reserved-concept guard, so the three public entry
    /// points (tag, pattern, attribute) explain the rule identically.
    /// </summary>
    internal static string ReservedConceptMessage(string concept)
        => $"Concept '{concept}' starts with '{ReservedConceptPrefix}', which is reserved for " +
           "framework-internal tags and cannot be used by application code. Alberto currently " +
           $"uses '{SchemaVersionConcept}' to carry the event schema version — declare that with " +
           "[EventType(\"...\", Version = N)] rather than by writing the tag yourself.";

    /// <summary>
    /// Creates the reserved schema-version tag for a given version number.
    /// Internal use only — called by the framework's append path.
    /// </summary>
    internal static EventTag ForVersion(int version)
        => FromStorage(SchemaVersionConcept, version.ToString());

    /// <summary>
    /// Private non-validating constructor. Callers MUST guarantee that concept and id
    /// are already valid (e.g., data read from the database, or split from a value that was
    /// already validated).
    /// </summary>
    private EventTag(string concept, string id, string precomputedValue)
    {
        Concept = concept;
        Id = id;
        _value = precomputedValue;
    }

    /// <summary>
    /// Creates an <see cref="EventTag"/> from data that is already known to be valid,
    /// skipping the validation regex and the reserved-concept guard. Use this when
    /// constructing tags from storage rows — including the framework-internal
    /// <see cref="SchemaVersionConcept"/> tag — to avoid redundant work on the read
    /// hot-path.
    /// </summary>
    /// <param name="concept">The concept component, already validated.</param>
    /// <param name="id">The id component, already validated.</param>
    /// <remarks>
    /// This overload is intentionally internal. It is the only sanctioned way to
    /// materialise a reserved-concept tag without going through
    /// <see cref="ForVersion"/>, and it is deliberately hidden from application code
    /// to prevent a caller from injecting a second <c>_version</c> entry that would
    /// permanently mis-version an event.  Framework code in friend assemblies
    /// (Postgres backend, in-memory backend) may call it on data already present in
    /// storage; they must not call it with user-supplied values.
    /// </remarks>
    internal static EventTag FromStorage(string concept, string id)
        => new(concept, id, string.Concat(concept, ":", id));

    /// <summary>
    /// Creates an <see cref="EventTag"/> from a "concept:id" string that is already known
    /// to be valid, skipping the validation regex and the reserved-concept guard. Use this
    /// when constructing tags from storage rows to avoid redundant work on the read
    /// hot-path.
    /// </summary>
    /// <param name="tag">A string in "concept:id" format, already validated.</param>
    /// <remarks>
    /// This overload is intentionally internal for the same reasons as
    /// <see cref="FromStorage(string, string)"/> — see its remarks.
    /// </remarks>
    internal static EventTag FromStorage(string tag)
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
    /// <exception cref="ArgumentException">
    /// Thrown when the concept component starts with <see cref="ReservedConceptPrefix"/> — that
    /// space is reserved for framework-internal use.
    /// </exception>
    public static EventTag Parse(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException("Tag cannot be null or empty.", nameof(tag));

        var colonIndex = tag.IndexOf(':');
        if (colonIndex < 0)
            throw new ArgumentException(
                $"Invalid tag format: '{tag}'. Expected format: concept:id",
                nameof(tag));

        // The EventTag(concept, id) public constructor enforces the reservation check.
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

        // Reserved concepts must not be surfaced to user code, even on the non-throwing path.
        if (IsReservedConcept(concept))
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
