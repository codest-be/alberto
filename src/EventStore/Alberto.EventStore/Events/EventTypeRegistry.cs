namespace Alberto.EventStore.Events;

public class EventTypeRegistry
{
    private readonly Dictionary<string, Type> _eventTypes = new();

    public EventTypeRegistry()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var types = assemblies.SelectMany(x => x.GetExportedTypes())
            .Where(type => type.GetCustomAttributes(typeof(EventType), false).Length > 0)
            .ToList();

        foreach (var type in types)
        {
            var name = EventType.GetEventType(type)!;
            _eventTypes.Add(name.Id, type);
        }
    }

    public Type GetEventType(string type)
    {
        return !_eventTypes.TryGetValue(type, out var eventType)
            ? throw new Exception($"Unknown Event type {type}")
            : eventType;
    }
}