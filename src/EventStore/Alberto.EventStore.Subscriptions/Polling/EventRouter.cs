using Microsoft.Extensions.Logging;
using System.Text.Json;
using Alberto.EventStore.Events;
using Alberto.EventStore.Serialization;
using Alberto.EventStore.Subscriptions.Checkpoints;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.EventStore.Subscriptions.Polling;

/// <summary>
/// Routes events to handlers with retry and poison pill support
/// </summary>
public sealed class EventRouter(
    object key,
    ICheckpointStore checkpointStore,
    IPoisonPillStore poisonPillStore,
    IServiceProvider serviceProvider,
    ILogger<EventRouter> logger,
    int maxRetries = 3,
    int retryDelayMs = 1000)
{
    private readonly List<HandlerRegistration> _handlers = new();

    public void RegisterHandler(HandlerRegistration handler)
    {
        _handlers.Add(handler);
    }

    public async Task InitializeHandlers(CancellationToken cancellationToken)
    {
        foreach (var handler in _handlers)
        {
            var checkpoint = await checkpointStore.GetLastCheckpoint(
                handler.SubscriptionId,
                cancellationToken
            );

            handler.Position = checkpoint.Position ?? -1;

            logger.LogInformation(
                "Initialized subscription '{SubscriptionId}' at position {Position}",
                handler.SubscriptionId,
                handler.Position
            );
        }
    }

    public async Task<bool> RouteEvent(
        GlobalEventEnvelope evt,
        CancellationToken cancellationToken)
    {
        var anyHandlerFailed = false;

        foreach (var handler in _handlers)
        {
            // Skip if handler already processed this event
            if (evt.GlobalPosition <= handler.Position)
                continue;

            // Check if handler is interested in this event type
            if (!handler.SupportedEventTypes.Contains(evt.EventType))
                continue;

            // Check for existing poison pill
            var existingPoisonPill = await poisonPillStore.GetPoisonPill(
                handler.SubscriptionId,
                evt.GlobalPosition,
                cancellationToken
            );

            if (existingPoisonPill != null && existingPoisonPill.ResolvedAt == null)
            {
                logger.LogError(
                    "Subscription '{SubscriptionId}' is blocked by unresolved poison pill at position {Position}",
                    handler.SubscriptionId,
                    evt.GlobalPosition
                );
                anyHandlerFailed = true;
                continue; // Don't process, subscription is blocked
            }

            // Try to process with retries
            var success = await ProcessEventWithRetry(
                handler,
                evt,
                cancellationToken
            );

            if (!success)
            {
                anyHandlerFailed = true;
                // Poison pill created, subscription will stop
            }
        }

        return !anyHandlerFailed;
    }

    private async Task<bool> ProcessEventWithRetry(
        HandlerRegistration handler,
        GlobalEventEnvelope evt,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    logger.LogWarning(
                        "Retrying event {EventId} for subscription '{SubscriptionId}' (attempt {Attempt}/{MaxRetries})",
                        evt.Id,
                        handler.SubscriptionId,
                        attempt,
                        maxRetries
                    );

                    await Task.Delay(retryDelayMs * attempt, cancellationToken);
                }

                await ProcessEvent(handler, evt, cancellationToken);

                // Success - advance checkpoint
                handler.Position = evt.GlobalPosition;
                handler.EventsProcessedSinceCheckpoint++;

                await checkpointStore.StoreCheckpoint(
                    new Checkpoint(handler.SubscriptionId, handler.Position, DateTimeOffset.UtcNow),
                    cancellationToken
                );

                return true;
            }
            catch (Exception ex)
            {
                lastException = ex;
                logger.LogError(
                    ex,
                    "Error processing event {EventId} for subscription '{SubscriptionId}' (attempt {Attempt}/{MaxRetries})",
                    evt.Id,
                    handler.SubscriptionId,
                    attempt + 1,
                    maxRetries + 1
                );
            }
        }

        // All retries exhausted - create poison pill
        await CreatePoisonPill(handler, evt, lastException!, cancellationToken);
        return false;
    }

    private async Task ProcessEvent(
        HandlerRegistration handler,
        GlobalEventEnvelope evt,
        CancellationToken cancellationToken)
    {
        var context = new EventContext(
            evt.GlobalPosition,
            evt.Id,
            evt.EventType,
            evt.TenantId,
            evt.Metadata,
            evt.Created
        );

        var deserializer = serviceProvider.GetRequiredService<IEventDeserializer>();
        var eventTypeRegistry = serviceProvider.GetRequiredService<EventTypeRegistry>();
        var eventType = eventTypeRegistry.GetEventType(evt.EventType);
        var eventInstance = deserializer.Deserialize(evt.EventJson, eventType);

        await using var scope = serviceProvider.CreateAsyncScope();
        // Execute through pipeline
        await scope.ServiceProvider.GetRequiredKeyedService<ConsumePipeline>(key).Execute(
            eventInstance,
            context,
            async () =>
            {
                // Call the typed handler method
                await InvokeTypedHandler(handler.Handler, eventInstance, context, cancellationToken);
            },
            cancellationToken
        );
    }

    private async ValueTask InvokeTypedHandler(
        IEventHandler handler,
        object eventInstance,
        EventContext context,
        CancellationToken cancellationToken)
    {
        var handlerType = handler.GetType();
        var handleInterfaces = handlerType
            .GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHandleEvent<>));

        foreach (var handleInterface in handleInterfaces)
        {
            // Check if this interface handles the event type we have
            var expectedEventType = handleInterface.GetGenericArguments()[0];
            var expectedEventTypeType = EventType.GetEventType(expectedEventType);

            if (expectedEventTypeType == null)
            {
                logger.LogWarning("Could not determine event type for {ExpectedEventType}", expectedEventType.FullName);
                continue;
            }

            var eventType = EventType.GetEventType(eventInstance.GetType());
            if (eventType == null)
            {
                logger.LogWarning("Could not determine event type for {EventInstanceType}", eventInstance.GetType().FullName);
                continue;
            }
            
            if (eventType.Equals(expectedEventTypeType))
            {
                var method = handleInterface.GetMethod(nameof(IHandleEvent<object>.Handle));
                if (method != null)
                {
                    var result = method.Invoke(handler, [eventInstance, context, cancellationToken]);
                    if (result is ValueTask valueTask)
                    {
                        await valueTask;
                    }
                }
            }
        }
    }

    private async Task CreatePoisonPill(
        HandlerRegistration handler,
        GlobalEventEnvelope evt,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var poisonPill = new PoisonPill(
            Guid.NewGuid(),
            handler.SubscriptionId,
            evt.GlobalPosition,
            evt.Id,
            evt.EventType,
            evt.EventJson,
            JsonSerializer.Serialize(evt.Metadata),
            exception.Message,
            exception.StackTrace,
            maxRetries + 1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null
        );

        await poisonPillStore.StorePoisonPill(poisonPill, cancellationToken);

        logger.LogCritical(
            "Created poison pill for subscription '{SubscriptionId}' at position {Position}. Subscription is now STOPPED.",
            handler.SubscriptionId,
            evt.GlobalPosition
        );
    }

    public long GetMinimumPosition()
    {
        if (_handlers.Count == 0)
            return -1;

        return _handlers.Min(h => h.Position);
    }

    public IReadOnlyList<HandlerRegistration> GetHandlers() => _handlers.AsReadOnly();
}