using System.Reflection;
using System.Text.Json;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.Serialization;
using Alberto.EventStore.Subscriptions.Batching;
using Alberto.EventStore.Subscriptions.Channel;
using Alberto.EventStore.Subscriptions.Checkpoints;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.PoisonPills;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Polling;

/// <summary>
/// Routes events to handlers with retry and poison pill support
/// </summary>
public sealed class EventRouter(
    string moduleKey,
    object key,
    ICheckpointStore checkpointStore,
    IPoisonPillStore poisonPillStore,
    IServiceProvider serviceProvider,
    ILogger<EventRouter> logger,
    IMetricsRecorder metrics,
    int maxRetries = 3,
    int retryDelayMs = 1000)
{
    private readonly List<HandlerRegistration> _handlers = [];
    private readonly Dictionary<string, ProjectionAccumulator> _projectionAccumulators = new();

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
        }

        if (_handlers.Count > 0)
        {
            logger.LogInformation(
                "Initialized {HandlerCount} subscription(s)",
                _handlers.Count
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

            if (existingPoisonPill is { ResolvedAt: null })
            {
                logger.LogError(
                    "Subscription '{SubscriptionId}' is blocked by unresolved poison pill at position {Position}",
                    handler.SubscriptionId,
                    evt.GlobalPosition
                );
                anyHandlerFailed = true;
                continue; // Don't process, subscription is blocked
            }

            // Route projections differently based on mode
            if (handler.IsProjection)
            {
                var success = await ProcessProjectionEvent(
                    handler,
                    evt,
                    cancellationToken);

                if (!success)
                {
                    anyHandlerFailed = true;
                }
            }
            else
            {
                // Regular event handler - existing flow
                var success = await ProcessEventWithRetry(
                    handler,
                    evt,
                    cancellationToken);

                if (!success)
                {
                    anyHandlerFailed = true;
                    // Poison pill created, subscription will stop
                }
            }
        }

        return !anyHandlerFailed;
    }

    public async Task<bool> RouteEvents(
        IReadOnlyList<GlobalEventEnvelope> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return true;

        var anyHandlerFailed = false;

        foreach (var evt in events)
        {
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

                if (existingPoisonPill is { ResolvedAt: null })
                {
                    logger.LogError(
                        "Subscription '{SubscriptionId}' is blocked by unresolved poison pill at position {Position}",
                        handler.SubscriptionId,
                        evt.GlobalPosition
                    );
                    anyHandlerFailed = true;
                    continue; // Don't process, subscription is blocked
                }

                // Try to process with retries (may accumulate in batch scope if present)
                var success = await ProcessEventWithRetry(
                    handler,
                    evt,
                    cancellationToken
                );

                if (!success)
                {
                    anyHandlerFailed = true;
                    // Poison pill created, subscription will stop
                    return false;
                }
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
        using var processingScope = metrics.RecordEventProcessing(handler.SubscriptionId, evt.EventType);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    // Record retry metric
                    metrics.RecordRetry(handler.SubscriptionId, evt.EventType, attempt);

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

                // Write checkpoint to ThrottledCheckpointStore (which batches database writes)
                await checkpointStore.StoreCheckpoint(
                    new Checkpoint(handler.SubscriptionId, handler.Position, DateTimeOffset.UtcNow),
                    cancellationToken
                );

                // Record success metrics
                metrics.RecordEventProcessed(handler.SubscriptionId, evt.EventType, evt.Created);

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

        // All retries exhausted - record failure and create poison pill
        metrics.RecordEventProcessingFailed(handler.SubscriptionId, evt.EventType);
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
            handler.SubscriptionId,
            evt.Metadata,
            evt.Created
        );

        var deserializer = serviceProvider.GetRequiredService<IEventDeserializer>();
        var eventTypeRegistry = serviceProvider.GetRequiredService<EventTypeRegistry>();
        var eventType = eventTypeRegistry.GetEventType(evt.EventType);
        var eventInstance = deserializer.Deserialize(evt.EventJson, eventType);

        var scope = serviceProvider.CreateAsyncScope();
        try
        {
            // Execute through pipeline
            await scope.ServiceProvider.GetRequiredKeyedService<ConsumePipeline>(key).Execute(
                eventInstance,
                context,
                async () =>
                {
                    // Resolve handler from the same scope
                    var handlerInstance =
                        scope.ServiceProvider.GetRequiredKeyedService(handler.HandlerType, moduleKey) as IEventHandler;
                    await InvokeTypedHandler(handlerInstance!, eventInstance, context, cancellationToken);
                },
                cancellationToken
            );
        }
        finally
        {
            await scope.DisposeAsync();
        }
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
                logger.LogWarning("Could not determine event type for {EventInstanceType}",
                    eventInstance.GetType().FullName);
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
            Guid.CreateVersion7(),
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

        // Record poison pill metric
        metrics.RecordPoisonPill(handler.SubscriptionId, evt.EventType);

        logger.LogCritical(
            "Created poison pill for subscription '{SubscriptionId}' at position {Position}. Subscription is now STOPPED.",
            handler.SubscriptionId,
            evt.GlobalPosition
        );
    }

    private async Task<bool> ProcessProjectionEvent(
        HandlerRegistration handler,
        GlobalEventEnvelope evt,
        CancellationToken ct)
    {
        // Get or create accumulator for this handler
        if (!_projectionAccumulators.TryGetValue(handler.SubscriptionId, out var accumulator))
        {
            accumulator = new ProjectionAccumulator();
            _projectionAccumulators[handler.SubscriptionId] = accumulator;
        }

        // Accumulate the event
        accumulator.Add(evt);

        // Decide whether to flush based on mode and thresholds
        var shouldFlush = ShouldFlushProjection(handler, accumulator);

        if (shouldFlush)
        {
            return await FlushProjectionAccumulator(handler, accumulator, ct);
        }

        return true; // Accumulated successfully
    }

    private bool ShouldFlushProjection(
        HandlerRegistration handler,
        ProjectionAccumulator accumulator)
    {
        var options = handler.ProjectionBatchingOptions;

        return handler.SubscriptionMode switch
        {
            // Sync: Always flush immediately for strong consistency
            SubscriptionMode.Sync => true,

            // Async: Flush when threshold met
            SubscriptionMode.Async =>
                accumulator.EventCount >= options.MaxBatchSize ||
                accumulator.TimeSinceFirstEvent >= options.MaxBatchTime,

            // Hybrid: Same as async (handler-level mode determines behavior)
            _ => true
        };
    }

    private async Task<bool> FlushProjectionAccumulator(
        HandlerRegistration handler,
        ProjectionAccumulator accumulator,
        CancellationToken ct)
    {
        var events = accumulator.GetEvents();
        if (events.Count == 0)
            return true;

        try
        {
            using var scope = serviceProvider.CreateAsyncScope();

            // Resolve the projection subscription
            var projectionInstance = scope.ServiceProvider.GetRequiredKeyedService(
                handler.HandlerType,
                moduleKey) as IProjectionSubscription;

            if (projectionInstance == null)
            {
                logger.LogError(
                    "Handler {HandlerType} is marked as projection but doesn't implement IProjectionSubscription",
                    handler.HandlerType.Name);
                return false;
            }

            // Group events by key
            var eventsByKey = new Dictionary<object, List<(object Event, EventContext Context)>>();

            foreach (var evt in events)
            {
                var eventInstance = DeserializeEvent(evt);
                var context = CreateEventContext(evt, handler.SubscriptionId);

                // Get keys from projection
                var keys = projectionInstance.GetKeys(eventInstance);

                foreach (var key in keys)
                {
                    if (!eventsByKey.TryGetValue(key, out var list))
                    {
                        list = new();
                        eventsByKey[key] = list;
                    }

                    list.Add((eventInstance, context));
                }
            }

            // Invoke the typed flush method
            await InvokeTypedFlush(projectionInstance, eventsByKey, ct);

            // Update checkpoint to max position
            var maxPosition = events.Max(e => e.GlobalPosition);
            handler.Position = maxPosition;
            await checkpointStore.StoreCheckpoint(
                new Checkpoint(handler.SubscriptionId, maxPosition, DateTimeOffset.UtcNow),
                ct);

            logger.LogDebug(
                "Flushed projection batch for {SubscriptionId}: {EventCount} events, {KeyCount} keys",
                handler.SubscriptionId,
                events.Count,
                eventsByKey.Count);

            accumulator.Clear();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to flush projection batch for {SubscriptionId}", handler.SubscriptionId);
            await CreatePoisonPill(handler, events[0], ex, ct);
            return false;
        }
    }

    private async Task InvokeTypedFlush(
        IProjectionSubscription projectionInstance,
        Dictionary<object, List<(object Event, EventContext Context)>> eventsByKey,
        CancellationToken ct)
    {
        // Use reflection to find the typed projection interface
        // It will be in Alberto.EventSourcing assembly: IProjectionSubscription<TKey, TState>
        var projectionType = projectionInstance.GetType();
        var projectionInterface = projectionType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                                 i.Name.StartsWith("IProjectionSubscription") &&
                                 i.GetGenericArguments().Length == 2);

        if (projectionInterface == null)
        {
            throw new InvalidOperationException(
                $"Projection {projectionType.Name} doesn't implement typed IProjectionSubscription<TKey, TState>. " +
                "Ensure your subscription implements Alberto.EventSourcing.Projections.IProjectionSubscription<TKey, TState>.");
        }

        var genericArgs = projectionInterface.GetGenericArguments();
        var keyType = genericArgs[0];
        var stateType = genericArgs[1];

        // Get repository and projector from the projection instance via reflection
        var repositoryProp = projectionInterface.GetProperty("Repository");
        var projectorProp = projectionInterface.GetProperty("Projector");

        if (repositoryProp == null || projectorProp == null)
        {
            throw new InvalidOperationException(
                $"Projection {projectionType.Name} doesn't expose Repository and Projector properties");
        }

        var repository = repositoryProp.GetValue(projectionInstance);
        var projector = projectorProp.GetValue(projectionInstance);

        // Invoke the generic batch processing method
        var method = typeof(EventRouter).GetMethod(
            nameof(FlushTypedProjectionBatch),
            BindingFlags.NonPublic | BindingFlags.Instance);

        var genericMethod = method!.MakeGenericMethod(keyType, stateType);
        var task = (Task)genericMethod.Invoke(this, [repository, projector, eventsByKey, ct])!;
        await task;
    }

    private async Task FlushTypedProjectionBatch<TKey, TState>(
        object repositoryObj,
        object projectorObj,
        Dictionary<object, List<(object Event, EventContext Context)>> eventsByKey,
        CancellationToken ct)
        where TKey : notnull
        where TState : new()
    {
        // Use reflection to call methods on repository and projector
        // Repository type: IProjectionRepository<TKey, TState>
        // Projector type: IProjector<TState>

        var repositoryType = repositoryObj.GetType();
        var projectorType = projectorObj.GetType();

        // Convert dictionary keys to typed version
        var typedEventsByKey = eventsByKey.ToDictionary(
            kvp => (TKey)kvp.Key,
            kvp => kvp.Value);

        // 1. Batch load current states - use reflection to call BatchGet
        var batchGetMethod = repositoryType.GetMethod("BatchGet");
        var batchGetTask = (Task)batchGetMethod!.Invoke(repositoryObj, [typedEventsByKey.Keys, ct])!;
        await batchGetTask;
        var currentStatesObj = batchGetTask.GetType().GetProperty("Result")!.GetValue(batchGetTask);
        var currentStates = (IDictionary<TKey, TState?>)currentStatesObj!;

        // 2. Fold events per key using projector.Apply
        var applyMethod = projectorType.GetMethod("Apply");
        var updates = new Dictionary<TKey, (TState State, long Version)>();

        foreach (var (key, events) in typedEventsByKey)
        {
            var currentState = currentStates.TryGetValue(key, out var existing) && existing != null
                ? existing
                : new TState();

            var newState = events.Aggregate(
                currentState,
                (state, tuple) => (TState)applyMethod!.Invoke(projectorObj, [state, tuple.Event])!);

            var maxVersion = events.Max(e => e.Context.GlobalPosition);
            updates[key] = (newState, maxVersion);
        }

        // 3. Batch save - use reflection to call BatchUpsertWithVersion
        var batchUpsertMethod = repositoryType.GetMethod("BatchUpsertWithVersion");
        var batchUpsertTask = (Task)batchUpsertMethod!.Invoke(repositoryObj, [updates, ct])!;
        await batchUpsertTask;
    }

    private object DeserializeEvent(GlobalEventEnvelope evt)
    {
        var deserializer = serviceProvider.GetRequiredService<IEventDeserializer>();
        var eventTypeRegistry = serviceProvider.GetRequiredService<EventTypeRegistry>();
        var eventType = eventTypeRegistry.GetEventType(evt.EventType);
        return deserializer.Deserialize(evt.EventJson, eventType);
    }

    private EventContext CreateEventContext(GlobalEventEnvelope evt, string subscriptionId)
    {
        return new EventContext(
            evt.GlobalPosition,
            evt.Id,
            evt.EventType,
            evt.TenantId,
            subscriptionId,
            evt.Metadata,
            evt.Created);
    }

    public long GetMinimumPosition()
    {
        if (_handlers.Count == 0)
            return -1;

        return _handlers.Min(h => h.Position);
    }

    public IReadOnlyList<HandlerRegistration> GetHandlers() => _handlers.AsReadOnly();
}