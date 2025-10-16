using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.ComponentTests;

/// <summary>
/// Registry that tracks which subscriptions handle which event types.
/// Used to determine when all relevant subscriptions have processed an event.
/// </summary>
public sealed class SubscriptionMetadataRegistry
{
    private readonly Dictionary<string, HashSet<string>> _subscriptionEventTypes = new();
    private readonly object _syncRoot = new();

    /// <summary>
    /// Registers a subscription with the event types it handles.
    /// </summary>
    public void RegisterSubscription(string subscriptionId, IEnumerable<string> eventTypes)
    {
        lock (_syncRoot)
        {
            _subscriptionEventTypes[subscriptionId] = new HashSet<string>(eventTypes);
        }
    }

    /// <summary>
    /// Gets all subscription IDs that handle the given event type.
    /// </summary>
    public IReadOnlySet<string> GetSubscriptionsFor(string eventTypeName)
    {
        lock (_syncRoot)
        {
            var result = new HashSet<string>();
            foreach (var (subscriptionId, eventTypes) in _subscriptionEventTypes)
            {
                if (eventTypes.Contains(eventTypeName))
                {
                    result.Add(subscriptionId);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Gets the count of subscriptions that handle the given event type.
    /// </summary>
    public int GetSubscriptionCountFor(string eventTypeName)
    {
        return GetSubscriptionsFor(eventTypeName).Count;
    }
}

public sealed class SubscriptionEventCollectorFilter(SubscriptionEventCollector collector) : IConsumeFilter
{
    public async ValueTask Execute(
        object @event,
        EventContext context,
        Func<ValueTask> next,
        CancellationToken cancellationToken = default)
    {
        // Execute the next filter/handler in the pipeline
        await next();

        collector.AddProcessedEvent(
            context.SubscriptionName,
            context.EventType,
            @event,
            context.GlobalPosition);
    }
}

/// <summary>
/// Collects processed subscription events for test assertions.
/// Allows tests to wait for specific events to be processed by subscriptions.
/// </summary>
public sealed class SubscriptionEventCollector
{
    private readonly List<ProcessedEvent> _processedEvents = [];
    private readonly object _syncRoot = new();
    private SubscriptionMetadataRegistry? _metadataRegistry;

    /// <summary>
    /// Sets the metadata registry for subscription tracking.
    /// </summary>
    public void SetMetadataRegistry(SubscriptionMetadataRegistry registry)
    {
        _metadataRegistry = registry;
    }

    public void AddProcessedEvent(string subscriptionId, string eventType, object @event, long globalPosition)
    {
        lock (_syncRoot)
        {
            _processedEvents.Add(new ProcessedEvent(
                subscriptionId,
                eventType,
                @event,
                globalPosition,
                DateTimeOffset.UtcNow
            ));
        }
    }

    /// <summary>
    /// Waits for an event of type T to be processed by ALL relevant subscriptions that match the predicate.
    /// </summary>
    /// <typeparam name="T">The event type to wait for</typeparam>
    /// <param name="predicate">Predicate to match the event</param>
    /// <param name="waitTimeout">Maximum time to wait</param>
    /// <returns>The matching event</returns>
    /// <exception cref="TimeoutException">Thrown if no matching event is found within the timeout</exception>
    public T WaitForEvent<T>(Func<T, bool> predicate, TimeSpan? waitTimeout = null)
        where T : class
    {
        var timeout = waitTimeout ?? TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(timeout);

        // Get event type name for subscription lookup
        var eventTypeName = typeof(T).Name;

        // Determine which subscriptions should process this event
        var expectedSubscriptions = _metadataRegistry?.GetSubscriptionsFor(eventTypeName);
        var waitForAllSubscriptions = expectedSubscriptions?.Count > 0;

        while (!cts.IsCancellationRequested)
        {
            lock (_syncRoot)
            {
                // Find matching events
                var matchingEvents = _processedEvents
                    .Where(pe => pe.Event is T evt && predicate(evt))
                    .ToList();

                if (matchingEvents.Count == 0)
                {
                    // No matching events yet
                    Thread.Sleep(1);
                    continue;
                }

                // If no metadata registry or no subscriptions registered, fall back to old behavior
                if (!waitForAllSubscriptions)
                {
                    return (T)matchingEvents.First().Event;
                }

                // Check if all expected subscriptions have processed a matching event
                var subscriptionsThatProcessed = matchingEvents
                    .Select(pe => pe.SubscriptionId)
                    .ToHashSet();

                if (expectedSubscriptions!.All(sub => subscriptionsThatProcessed.Contains(sub)))
                {
                    // All subscriptions have processed the event!
                    return (T)matchingEvents.First().Event;
                }
            }

            Thread.Sleep(1);
        }

        var subscriptionInfo = waitForAllSubscriptions
            ? $" by all {expectedSubscriptions!.Count} subscriptions ({string.Join(", ", expectedSubscriptions)})"
            : "";

        throw new TimeoutException(
            $"Event {eventTypeName} matching predicate was not processed{subscriptionInfo} within the timeout of {timeout.TotalSeconds} seconds.");
    }

    /// <summary>
    /// Waits for any event of type T to be processed.
    /// </summary>
    public T WaitForEvent<T>(TimeSpan? waitTimeout = null) where T : class
    {
        return WaitForEvent<T>(_ => true, waitTimeout);
    }

    /// <summary>
    /// Clears all collected events. Useful between test scenarios.
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
        {
            _processedEvents.Clear();
        }
    }

    private record ProcessedEvent(
        string SubscriptionId,
        string EventType,
        object Event,
        long GlobalPosition,
        DateTimeOffset ProcessedAt);
}