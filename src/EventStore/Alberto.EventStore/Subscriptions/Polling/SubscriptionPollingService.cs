using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Subscriptions.Batching;
using Alberto.EventStore.Subscriptions.DistributedLocking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Polling;

/// <summary>
/// Background service that polls for events and routes them to handlers.
/// Uses distributed locking to ensure only one instance actively polls at a time.
/// </summary>
public sealed class SubscriptionPollingService(
    string moduleKey,
    EventRouter eventRouter,
    PollingOptions options,
    IDistributedLock distributedLock,
    IServiceProvider serviceProvider,
    ILogger<SubscriptionPollingService> logger,
    IMetricsRecorder metrics)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting subscription polling service with distributed locking");

        // Acquire distributed lock before starting polling
        var lockRetryInterval = options.LockAcquisitionRetryIntervalMs;
        while (!stoppingToken.IsCancellationRequested)
        {
            var lockAcquired = await distributedLock.TryAcquireLockAsync(stoppingToken);
            if (lockAcquired)
            {
                logger.LogInformation("Successfully acquired distributed lock. Starting event polling.");
                break;
            }

            logger.LogDebug(
                "Failed to acquire distributed lock. Retrying in {RetryInterval}ms...",
                lockRetryInterval
            );

            await Task.Delay(lockRetryInterval, stoppingToken);

            // Exponential backoff
            lockRetryInterval = Math.Min(
                (int)(lockRetryInterval * options.LockRetryBackoffFactor),
                options.LockAcquisitionMaxRetryIntervalMs
            );
        }

        if (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Subscription polling service cancelled before acquiring lock");
            return;
        }

        try
        {
            // Initialize handler checkpoints
            await eventRouter.InitializeHandlers(stoppingToken);

            var currentPollingInterval = options.MinPollingIntervalMs;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var fromPosition = eventRouter.GetMinimumPosition();

                    IReadOnlyCollection<GlobalEventEnvelope> events;
                    await using (var scope = serviceProvider.CreateAsyncScope())
                    {
                        events = await scope.ServiceProvider
                            .GetRequiredKeyedService<IMultiTenantEventStore>(moduleKey)
                            .StreamAll(
                                fromPosition,
                                options.MaxPageSize,
                                eventTypes: null, // Get all event types
                                stoppingToken
                            );
                    }

                    if (events.Count > 0)
                    {
                        logger.LogDebug(
                            "Retrieved {EventCount} events from position {FromPosition}",
                            events.Count,
                            fromPosition
                        );

                        // Record batch size metric
                        metrics.RecordPollingBatch(moduleKey, events.Count);

                        // Route events in batch with projection batching scope
                        var orderedEvents = events.OrderBy(e => e.GlobalPosition).ToList();

                        await using (var batchScope = new ProjectionBatchScope())
                        {
                            var success = await eventRouter.RouteEvents(orderedEvents, stoppingToken);

                            if (!success)
                            {
                                logger.LogWarning("Stopping polling due to subscription failures");
                                break;
                            }

                            // Batch scope will auto-commit on dispose
                        }

                        // Reset polling interval on successful processing
                        currentPollingInterval = options.MinPollingIntervalMs;
                    }
                    else
                    {
                        // No events, increase polling interval
                        currentPollingInterval = Math.Min(
                            (int)(currentPollingInterval * options.PollingGrowFactor),
                            options.MaxPollingIntervalMs
                        );
                    }

                    // Update polling interval metric
                    metrics.UpdatePollingInterval(moduleKey, currentPollingInterval);

                    await Task.Delay(currentPollingInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in subscription polling service");

                    // Wait before retrying
                    await Task.Delay(options.MaxPollingIntervalMs, stoppingToken);
                }
            }
        }
        finally
        {
            // Release distributed lock on shutdown
            logger.LogInformation("Releasing distributed lock");
            await distributedLock.ReleaseLockAsync();
        }

        logger.LogInformation("Subscription polling service stopped");
    }
}