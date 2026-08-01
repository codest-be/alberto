using System.Text.RegularExpressions;

namespace Alberto;

/// <summary>
/// Attribute to specify the event type identifier for an event class.
/// The event type is used for filtering and routing events.
/// </summary>
/// <remarks>
/// Event-type slugs are restricted to <b>lowercase</b> letters, digits, hyphens, and
/// underscores (<c>[a-z0-9_-]+</c>). This is deliberately more restrictive than
/// <see cref="EventTag"/>, which also accepts uppercase letters in both concept and id.
/// The lowercase requirement makes event types case-insensitive by construction, avoids
/// ambiguity in type registries, and mirrors common slug conventions in messaging systems.
/// </remarks>
/// <example>
/// <code>
/// [EventType("order-placed")]
/// public record OrderPlaced(Guid OrderId, decimal Amount) : IEvent;
///
/// // With an explicit schema version:
/// [EventType("order-placed", Version = 2)]
/// public record OrderPlaced(Guid OrderId, decimal Amount, string Region) : IEvent;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed partial class EventTypeAttribute : Attribute
{
    public EventTypeAttribute(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Event type ID cannot be null or whitespace.", nameof(id));

        if (!EventTypeRegex().IsMatch(id))
            throw new ArgumentException(
                $"Event type '{id}' is invalid. Only lowercase letters, numbers, hyphens, and underscores are allowed.",
                nameof(id));

        Id = id;
    }

    /// <summary>
    /// The unique identifier for this event type (e.g., "order-placed", "customer_created").
    /// </summary>
    public string Id { get; }

    private int _version = 1;

    /// <summary>
    /// The schema version of this event type. Defaults to 1.
    /// Must be &gt;= 1. Used to populate the reserved <c>_version:N</c> tag on every persisted event,
    /// and to drive the upcaster chain on read.
    /// </summary>
    /// <remarks>
    /// <b>Important</b>: version is <em>not</em> part of the persisted <c>event_type</c> column
    /// and does <em>not</em> affect <see cref="EventType"/> equality or hashing.
    /// Changing this value on a type that already has data in the store without registering
    /// a corresponding upcaster will cause deserialization failures on read.
    /// </remarks>
    public int Version
    {
        get => _version;
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(Version),
                    $"Event schema version must be >= 1, but was {value}.");
            _version = value;
        }
    }

    /// <summary>
    /// States that events stored at an older version of this type deserialize correctly into the
    /// current shape without an upcaster, so the version gap may be read directly. Defaults to
    /// <see langword="false"/>, which is the safe answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both the startup validator (<c>ALB0018</c>) and <see cref="EventSerializer.Deserialize"/>
    /// refuse a version gap they have no upcaster for, because the default JSON behaviour for a
    /// missing property is to leave it at its CLR default — an evolver then folds a zero, an empty
    /// string or a <see langword="null"/> as though it had been stored, and the corruption is
    /// silent. This flag is the escape hatch for the one bump where that is genuinely fine:
    /// <b>a purely additive change whose new members are optional and whose default is the value
    /// you want for events written before the change</b>.
    /// </para>
    /// <para>
    /// It is a claim about the JSON, not about the C#. Adding a required member, renaming one,
    /// changing a type, or narrowing a meaning all fail this test even though they compile — write
    /// an upcaster for those. Setting the flag to avoid writing one is how stale state gets into a
    /// projection you will later have to rebuild from a log that never recorded the difference.
    /// </para>
    /// <example>
    /// <code>
    /// // v1 had no Region. Every order written before the bump was placed in "eu-west-1", which
    /// // is exactly what the property defaults to — so no upcaster is needed to read them.
    /// [EventType("order-placed", Version = 2, UpcastingNotRequired = true)]
    /// public record OrderPlaced(Guid OrderId, decimal Amount, string Region = "eu-west-1") : IEvent;
    /// </code>
    /// </example>
    /// </remarks>
    public bool UpcastingNotRequired { get; set; }

    /// <summary>
    /// Gets the event type attribute from a type, if present.
    /// </summary>
    public static EventTypeAttribute? GetEventType(Type type)
    {
        var attribute = type
            .GetCustomAttributes(typeof(EventTypeAttribute), false)
            .Cast<EventTypeAttribute>()
            .FirstOrDefault();

        return attribute;
    }

    /// <summary>
    /// Gets the event type ID from a type, throwing if not found.
    /// </summary>
    public static string GetEventTypeId(Type type)
    {
        var attribute = GetEventType(type);
        if (attribute is null)
            throw new InvalidOperationException(
                $"Type '{type.FullName}' does not have an [EventType] attribute.");

        return attribute.Id;
    }

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex EventTypeRegex();
}

/// <summary>
/// Represents an event type identifier as a value type.
/// Used in queries and event metadata.
/// </summary>
/// <remarks>
/// <para>
/// The identifier string passed to the constructor is <b>not</b> validated against the
/// lowercase-only rule enforced by <see cref="EventTypeAttribute"/>; validation happens
/// at attribute construction time (decoration site). When creating an <see cref="EventType"/>
/// manually, follow the same convention: lowercase letters, digits, hyphens, and underscores
/// only (e.g., <c>"order-placed"</c>, <c>"customer_created"</c>).
/// </para>
/// <para>
/// <b>Version is intentionally excluded from equality and hashing.</b>
/// <see cref="DcbQuery.ByTypes(EventType[])"/> uses <see cref="EventType"/> as a filter key, and the
/// <c>alberto_event_type_positions</c> index is keyed on <c>event_type</c> (the ID string).
/// Including version in equality would silently break consistency boundaries — a boundary
/// declared as <c>ByTypes("order-placed")</c> would stop matching v2 events.
/// </para>
/// </remarks>
public readonly struct EventType : IEquatable<EventType>
{
    public EventType(string id) : this(id, 1) { }

    public EventType(string id, int version)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Event type ID cannot be null or whitespace.", nameof(id));

        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version),
                $"Event type version must be >= 1, but was {version}.");

        Id = id;
        Version = version;
    }

    /// <summary>
    /// The event type identifier. This is the value persisted in the <c>event_type</c> column.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The schema version of this event. Defaults to 1.
    /// This value is carried via the reserved <c>_version:N</c> tag and is NOT part of equality,
    /// hashing, or the persisted <c>event_type</c> column value.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// Creates an EventType from a CLR type that has the [EventType] attribute.
    /// The <see cref="Version"/> is read from <see cref="EventTypeAttribute.Version"/>.
    /// </summary>
    public static EventType FromType<T>() where T : IEvent
        => FromType(typeof(T));

    /// <summary>
    /// Creates an EventType from a CLR type that has the [EventType] attribute.
    /// The <see cref="Version"/> is read from <see cref="EventTypeAttribute.Version"/>.
    /// </summary>
    public static EventType FromType(Type type)
    {
        var attr = EventTypeAttribute.GetEventType(type)
            ?? throw new InvalidOperationException(
                $"Type '{type.FullName}' does not have an [EventType] attribute.");
        return new EventType(attr.Id, attr.Version);
    }

    /// <summary>
    /// Equality and hashing are based solely on <see cref="Id"/>.
    /// <see cref="Version"/> is deliberately excluded — see the class-level remarks.
    /// </summary>
    public bool Equals(EventType other) => string.Equals(Id, other.Id, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is EventType other && Equals(other);
    public override int GetHashCode() => Id?.GetHashCode() ?? 0;

    /// <summary>Returns the event type ID — the value that is persisted to storage.</summary>
    public override string ToString() => Id;

    public static bool operator ==(EventType left, EventType right) => left.Equals(right);
    public static bool operator !=(EventType left, EventType right) => !left.Equals(right);

    /// <summary>
    /// Implicit conversion returns <see cref="Id"/> only — version is never part of the
    /// persisted or query string representation.
    /// </summary>
    public static implicit operator string(EventType eventType) => eventType.Id;
    public static implicit operator EventType(string id) => new(id);
}
