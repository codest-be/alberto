using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.ComponentTests;

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

    public void AddProcessedEvent(string eventType, object @event, long globalPosition)
    {
        lock (_syncRoot)
        {
            _processedEvents.Add(new ProcessedEvent(
                eventType,
                @event,
                globalPosition,
                DateTimeOffset.UtcNow
            ));
        }
    }

    /// <summary>
    /// Waits for an event of type T to be processed that matches the predicate.
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

        while (!cts.IsCancellationRequested)
        {
            lock (_syncRoot)
            {
                var matchingEvent = _processedEvents
                    .Where(pe => pe.Event is T)
                    .Select(pe => pe.Event as T)
                    .FirstOrDefault(e => predicate(e!));

                if (matchingEvent is not null)
                    return matchingEvent;
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            $"Event {typeof(T).Name} matching predicate was not processed within the timeout of {timeout.TotalSeconds} seconds.");
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
        string EventType,
        object Event,
        long GlobalPosition,
        DateTimeOffset ProcessedAt);
}