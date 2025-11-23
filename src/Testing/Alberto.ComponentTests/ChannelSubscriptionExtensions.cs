using System.Reflection;
using Alberto.EventStore;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Projections.InMemory;

namespace Alberto.ComponentTests;

public static class ChannelSubscriptionExtensions
{
    public static ChannelSubscriptionsBuilder<TEventStore> AddTestingProjection<TSubscription, TProjector, TEventStore>(
        this ChannelSubscriptionsBuilder<TEventStore> builder,
        SubscriptionMode mode,
        SubscriptionMetadataRegistry registry)
        where TSubscription : class, IProjectionSubscription, IEventHandler
        where TProjector : class
        where TEventStore : EventStoreFactory
    {
        var subscriptionAttribute = typeof(TSubscription).GetCustomAttribute<SubscriptionAttribute>();
        var subscriptionId = subscriptionAttribute?.SubscriptionId ?? typeof(TSubscription).Name;

        var handleInterfaces = typeof(TSubscription)
            .GetInterfaces()
            .Where(i => i.IsGenericType &&
                        i.GetGenericTypeDefinition() == typeof(IHandleEvent<>));

        var eventTypes = handleInterfaces
            .Select(i => i.GetGenericArguments()[0].Name)
            .ToArray();

        registry.RegisterSubscription(subscriptionId, eventTypes);
        return builder.AddInMemoryProjection<TSubscription, TProjector, TEventStore>(mode);
    }
}