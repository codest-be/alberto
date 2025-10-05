using System.Reflection;
using Alberto.EventStore.Events;

namespace Alberto.EventStore.Subscriptions.Subscriptions;

/// <summary>
/// Discovers event types that a handler supports
/// </summary>
public static class EventTypeDiscovery
{
    /// <summary>
    /// Discovers all event types a handler can handle
    /// </summary>
    public static HashSet<string> DiscoverEventTypes(Type handlerType)
    {
        var eventTypes = new HashSet<string>();

        // Find all IHandleEvent<T> interfaces
        var handleInterfaces = handlerType
            .GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHandleEvent<>));

        foreach (var handleInterface in handleInterfaces)
        {
            var eventType = handleInterface.GetGenericArguments()[0];

            // Get the event type name using EventType
            var eventTypeName = EventType.GetEventType(eventType);
            if (eventTypeName != null)
            {
                eventTypes.Add(eventTypeName.Id);
            }
        }

        return eventTypes;
    }

    /// <summary>
    /// Gets the subscription ID from the handler type's attribute
    /// </summary>
    public static string GetSubscriptionId(Type handlerType)
    {
        var attribute = handlerType.GetCustomAttribute<SubscriptionAttribute>();
        if (attribute == null)
        {
            throw new InvalidOperationException(
                $"Handler type '{handlerType.Name}' must have [Subscription] attribute"
            );
        }

        return attribute.SubscriptionId;
    }

    /// <summary>
    /// Creates a dictionary mapping event type names to .NET types
    /// </summary>
    public static Dictionary<string, Type> CreateEventTypeMap(Type handlerType)
    {
        var map = new Dictionary<string, Type>();

        var handleInterfaces = handlerType
            .GetInterfaces()
            .Where(i => i.IsGenericType &&
                        i.GetGenericTypeDefinition() == typeof(IHandleEvent<>));

        foreach (var handleInterface in handleInterfaces)
        {
            var eventType = handleInterface.GetGenericArguments()[0];
            var eventTypeName = EventType.GetEventType(eventType);

            if (eventTypeName != null)
            {
                map[eventTypeName.Id] = eventType;
            }
        }

        return map;
    }

    /// <summary>
    /// Discovers all event handler types in an assembly that implement IEventHandler and have the SubscriptionAttribute
    /// </summary>
    public static IEnumerable<Type> DiscoverHandlerTypes(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => typeof(IEventHandler).IsAssignableFrom(type))
            .Where(type => type.GetCustomAttribute<SubscriptionAttribute>() != null);
    }
}