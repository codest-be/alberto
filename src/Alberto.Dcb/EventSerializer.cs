using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Alberto.Dcb.Upcasting;

namespace Alberto.Dcb;

/// <summary>
/// JSON-based event serializer that uses [EventType] attributes to map between
/// event type IDs and CLR types. Build once per assembly, reuse across the lifetime of the app.
/// </summary>
public sealed class EventSerializer
{
    private readonly IReadOnlyDictionary<string, Type> _registry;
    private readonly JsonSerializerOptions _options;
    private readonly UpcasterRegistry? _upcasters;

    private EventSerializer(
        IReadOnlyDictionary<string, Type> registry,
        JsonSerializerOptions options,
        UpcasterRegistry? upcasters)
    {
        _registry = registry;
        _options = options;
        _upcasters = upcasters;
    }

    /// <summary>
    /// Creates an EventSerializer by scanning the given assemblies for all
    /// <see cref="IEvent"/> types that have an <see cref="EventTypeAttribute"/>.
    /// </summary>
    public static EventSerializer FromAssemblies(
        JsonSerializerOptions? options = null,
        params Assembly[] assemblies)
    {
        var registry = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IEvent).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => (type: t, attr: EventTypeAttribute.GetEventType(t)))
            .Where(x => x.attr is not null)
            .ToDictionary(x => x.attr!.Id, x => x.type);

        return new EventSerializer(
            registry,
            options ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            null);
    }

    /// <summary>
    /// Creates an EventSerializer by scanning a single assembly.
    /// </summary>
    public static EventSerializer FromAssembly(Assembly assembly, JsonSerializerOptions? options = null)
        => FromAssemblies(options, assembly);

    /// <summary>
    /// Creates an EventSerializer from an explicitly constructed type registry.
    /// Prefer <see cref="FromAssemblies"/> or <see cref="FromAssembly"/> for normal use;
    /// use this factory when you need tight control over which types are included
    /// (e.g. in tests that scan a large assembly with duplicate event-type IDs).
    /// </summary>
    internal static EventSerializer FromRegistry(
        IReadOnlyDictionary<string, Type> registry,
        JsonSerializerOptions? options = null,
        UpcasterRegistry? upcasters = null)
        => new(registry,
               options ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
               upcasters);

    /// <summary>
    /// Returns a new <see cref="EventSerializer"/> that applies the given
    /// <see cref="UpcasterRegistry"/> during <see cref="Deserialize"/>.
    /// </summary>
    public EventSerializer WithUpcasters(UpcasterRegistry upcasters)
    {
        ArgumentNullException.ThrowIfNull(upcasters);
        return new EventSerializer(_registry, _options, upcasters);
    }

    /// <summary>
    /// Deserializes an event envelope to its concrete CLR type using the registry.
    /// If an <see cref="UpcasterRegistry"/> is configured and the envelope carries a version
    /// lower than the chain's current version, the upcast chain is applied first.
    /// Throws <see cref="InvalidOperationException"/> if the event type is not registered.
    /// </summary>
    public IEvent Deserialize(IEventEnvelope envelope)
    {
        if (!_registry.TryGetValue(envelope.EventType.Id, out var type))
            throw new InvalidOperationException($"No registered type for event '{envelope.EventType.Id}'. " +
                $"Ensure the type has [EventType(\"{envelope.EventType.Id}\")] and its assembly was included when building the serializer.");

        var version = envelope.EventType.Version;

        // Apply upcasting if the envelope is at an older schema version.
        if (_upcasters is not null
            && _upcasters.TryGet(envelope.EventType.Id, out var upcaster)
            && upcaster is not null)
        {
            if (version > upcaster.CurrentVersion)
                throw new InvalidOperationException(
                    $"Upcaster for '{envelope.EventType.Id}' has no step for version {version}. " +
                    $"The chain covers up to version {upcaster.CurrentVersion}. " +
                    "This event was written by a newer version of the application.");

            if (version < upcaster.CurrentVersion)
                return upcaster.Apply(version, envelope.EventData, _options);

            // version == currentVersion: fall through to regular deserialization.
        }

        return (IEvent)(JsonSerializer.Deserialize(envelope.EventData, type, _options)
            ?? throw new InvalidOperationException($"Failed to deserialize event '{envelope.EventType.Id}'."));
    }

    /// <summary>
    /// Serializes an event to its JSON representation.
    /// </summary>
    public string Serialize(IEvent @event)
        => JsonSerializer.Serialize(@event, @event.GetType(), _options);

    /// <summary>
    /// Returns all event type IDs registered in this serializer.
    /// </summary>
    public IEnumerable<string> RegisteredTypeIds => _registry.Keys;

    private static readonly ConcurrentDictionary<Type, (PropertyInfo prop, string concept)[]> TagCache = new();

    /// <summary>
    /// Extracts <see cref="EventTag"/>s from an event using <see cref="TagAttribute"/> on properties,
    /// and appends the reserved schema-version tag <c>_version:N</c> derived from the type's
    /// <see cref="EventTypeAttribute.Version"/>.
    /// </summary>
    /// <param name="event">The event to extract tags from.</param>
    /// <param name="valueTransform">
    /// Optional transform: (concept, rawValue) => tagValue.
    /// Use this for values that need hashing or other normalization before becoming valid tag IDs.
    /// </param>
    public IReadOnlyCollection<EventTag> ExtractTags(
        IEvent @event,
        Func<string, string, string>? valueTransform = null)
    {
        var tagged = TagCache.GetOrAdd(@event.GetType(), static t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Select(p => (prop: p, attr: p.GetCustomAttribute<TagAttribute>()))
             .Where(x => x.attr is not null)
             .Select(x => (x.prop, x.attr!.Concept))
             .ToArray());

        var tags = new List<EventTag>(tagged.Length + 1);

        foreach (var (prop, concept) in tagged)
        {
            var value = prop.GetValue(@event);
            if (value is null) continue;

            var rawValue = value is Guid g ? g.ToString("D") : value.ToString()!;
            var tagValue = valueTransform is not null ? valueTransform(concept, rawValue) : rawValue;
            tags.Add(new EventTag(concept, tagValue));
        }

        // Stamp the reserved schema-version tag. The EventTypeAttribute is the single
        // authoritative source for the version; EventTag.ForVersion uses FromStorage so
        // it never triggers the public-constructor reservation guard.
        var attr = EventTypeAttribute.GetEventType(@event.GetType());
        var schemaVersion = attr?.Version ?? 1;
        tags.Add(EventTag.ForVersion(schemaVersion));

        return tags;
    }
}
