using System.Reflection;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Projections.InMemory;

namespace Alberto.ComponentTests;

public static class ChannelSubscriptionExtensions
{
    public static ChannelSubscriptionsBuilder<TEventStore> AddTestingProjection<TEventStore, TSubscription, TKey,
        TState, TProjector>(
        this ChannelSubscriptionsBuilder<TEventStore> builder,
        SubscriptionMode mode,
        SubscriptionMetadataRegistry registry)
        where TEventStore : EventStoreFactory
        where TSubscription : class, IEventHandler
        where TKey : notnull
        where TState : new()
        where TProjector : class, IProjector<TState>
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
        return builder.AddInMemoryProjection<TEventStore, TSubscription, TKey, TState, TProjector>(mode);
    }
}