using System.Collections.Concurrent;
using System.Threading.Channels;
using Alberto.EventStore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Channel;

/// <summary>
/// Registry for managing channel-based subscriptions with targeted event routing.
/// Only routes events to channels that have registered interest in specific event types.
/// </summary>
public sealed class ChannelSubscriptionRegistry(ILogger<ChannelSubscriptionRegistry> logger, IMetricsRecorder metrics)
{
    private readonly ConcurrentDictionary<string, ChannelSubscription> _subscriptions = new();

    /// <summary>
    /// Registers a channel subscription for a specific module
    /// </summary>
    /// <param name="moduleKey">Unique module identifier</param>
    /// <param name="writer">Channel writer for this subscription</param>
    /// <param name="eventTypeFilter">Optional event type filter. If null or empty, receives all events.</param>
    public void Register(
        string moduleKey,
        ChannelWriter<GlobalEventEnvelope> writer,
        IReadOnlySet<string>? eventTypeFilter = null)
    {
        var subscription = new ChannelSubscription(moduleKey, writer, eventTypeFilter);
        _subscriptions[moduleKey] = subscription;

        logger.LogInformation(
            "Registered channel subscription for module '{ModuleKey}' with filter: {Filter}",
            moduleKey,
            eventTypeFilter == null || eventTypeFilter.Count == 0
                ? "ALL EVENTS"
                : string.Join(", ", eventTypeFilter));
    }

    /// <summary>
    /// Unregisters a channel subscription
    /// </summary>
    /// <param name="moduleKey">Module identifier to unregister</param>
    public void Unregister(string moduleKey)
    {
        _subscriptions.TryRemove(moduleKey, out _);
    }

    /// <summary>
    /// Gets all channel writers that should receive events based on event types
    /// </summary>
    /// <param name="events">Events to be routed</param>
    /// <returns>Collection of channel writers that match the event types</returns>
    public IReadOnlyCollection<ChannelWriter<GlobalEventEnvelope>> GetMatchingChannels(
        IReadOnlyCollection<GlobalEventEnvelope> events)
    {
        if (events.Count == 0)
            return [];

        // Extract unique event types from the events
        var eventTypes = events.Select(e => e.EventType).ToHashSet();

        var matchingWriters = new List<ChannelWriter<GlobalEventEnvelope>>();

        foreach (var subscription in _subscriptions.Values)
        {
            // If no filter, subscription receives all events
            if (subscription.EventTypeFilter == null || subscription.EventTypeFilter.Count == 0)
            {
                matchingWriters.Add(subscription.Writer);
                continue;
            }

            // Check if any event type matches the subscription's filter
            if (eventTypes.Any(et => subscription.EventTypeFilter.Contains(et)))
            {
                matchingWriters.Add(subscription.Writer);
            }
        }

        return matchingWriters;
    }

    /// <summary>
    /// Notifies all matching subscriptions of newly appended events
    /// </summary>
    /// <param name="events">Events that were appended</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask NotifySubscriptions(
        IReadOnlyCollection<GlobalEventEnvelope> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
            return;

        var eventTypes = events.Select(e => e.EventType).Distinct().ToList();
        logger.LogDebug(
            "NotifySubscriptions called with {EventCount} events of types: {EventTypes}",
            events.Count,
            string.Join(", ", eventTypes));

        var matchingChannels = GetMatchingChannels(events);

        logger.LogDebug(
            "Found {MatchingChannelCount} matching channels out of {TotalSubscriptions} total subscriptions",
            matchingChannels.Count,
            _subscriptions.Count);

        if (matchingChannels.Count == 0)
            return;

        // Write to all matching channels in parallel
        var tasks = new List<ValueTask>(matchingChannels.Count * events.Count);
        var blockedWriteCount = 0;

        foreach (var writer in matchingChannels)
        {
            foreach (var evt in events)
            {
                // Try non-blocking write first
                if (writer.TryWrite(evt))
                {
                    continue;
                }

                // If channel is full, use async write
                tasks.Add(writer.WriteAsync(evt, cancellationToken));
                blockedWriteCount++;
            }
        }

        // Await all async writes
        foreach (var task in tasks)
        {
            await task;
        }

        // Record metrics
        foreach (var (moduleKey, subscription) in _subscriptions)
        {
            if (matchingChannels.Contains(subscription.Writer))
            {
                metrics.RecordChannelPublish(moduleKey, events.Count);
            }
        }

        if (blockedWriteCount > 0)
        {
            foreach (var (moduleKey, subscription) in _subscriptions)
            {
                if (matchingChannels.Contains(subscription.Writer))
                {
                    metrics.RecordChannelWriteBlocked(moduleKey);
                }
            }
        }

        logger.LogDebug(
            "Successfully notified {ChannelCount} channels with {EventCount} events",
            matchingChannels.Count,
            events.Count);
    }

    private sealed record ChannelSubscription(
        string ModuleKey,
        ChannelWriter<GlobalEventEnvelope> Writer,
        IReadOnlySet<string>? EventTypeFilter);
}