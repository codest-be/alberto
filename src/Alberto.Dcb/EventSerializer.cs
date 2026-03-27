using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Alberto.Dcb;

/// <summary>
/// JSON-based event serializer that uses [EventType] attributes to map between
/// event type IDs and CLR types. Build once per assembly, reuse across the lifetime of the app.
/// </summary>
public sealed class EventSerializer
{
    private readonly IReadOnlyDictionary<string, Type> _registry;
    private readonly JsonSerializerOptions _options;

    private EventSerializer(IReadOnlyDictionary<string, Type> registry, JsonSerializerOptions options)
    {
        _registry = registry;
        _options = options;
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

        return new EventSerializer(registry, options ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>
    /// Creates an EventSerializer by scanning a single assembly.
    /// </summary>
    public static EventSerializer FromAssembly(Assembly assembly, JsonSerializerOptions? options = null)
        => FromAssemblies(options, assembly);

    /// <summary>
    /// Deserializes an event envelope to its concrete CLR type using the registry.
    /// Throws <see cref="InvalidOperationException"/> if the event type is not registered.
    /// </summary>
    public IEvent Deserialize(IEventEnvelope envelope)
    {
        if (!_registry.TryGetValue(envelope.EventType.Id, out var type))
            throw new InvalidOperationException($"No registered type for event '{envelope.EventType.Id}'. " +
                $"Ensure the type has [EventType(\"{envelope.EventType.Id}\")] and its assembly was included when building the serializer.");

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
    /// Extracts EventTags from an event using [Tag("concept")] attributes on properties.
    /// Guid values are formatted as "D" (with dashes) to match TagKeys conventions.
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

        var tags = new List<EventTag>(tagged.Length);
        foreach (var (prop, concept) in tagged)
        {
            var value = prop.GetValue(@event);
            if (value is null) continue;

            var rawValue = value is Guid g ? g.ToString("D") : value.ToString()!;
            var tagValue = valueTransform is not null ? valueTransform(concept, rawValue) : rawValue;
            tags.Add(new EventTag(concept, tagValue));
        }
        return tags;
    }
}
