using System.Collections.Frozen;

namespace Alberto.EventStore.Events;

/// <summary>
/// Registry for event types, optimized for high-performance lookups using FrozenDictionary.
/// FrozenDictionary provides ~20-30% faster lookups compared to regular Dictionary for immutable collections.
/// </summary>
public class EventTypeRegistry
{
    private readonly FrozenDictionary<string, Type> _eventTypes;

    public EventTypeRegistry()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var types = assemblies.SelectMany(x => x.GetExportedTypes())
            .Where(type => type.GetCustomAttributes(typeof(EventType), false).Length > 0)
            .ToDictionary(
                type => EventType.GetEventType(type)!.Id,
                type => type
            );

        _eventTypes = types.ToFrozenDictionary();
    }

    public Type GetEventType(string type)
    {
        return !_eventTypes.TryGetValue(type, out var eventType)
            ? throw new Exception($"Unknown Event type {type}")
            : eventType;
    }
}