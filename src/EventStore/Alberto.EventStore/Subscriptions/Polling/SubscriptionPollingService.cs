using Alberto.EventStore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Polling;

/// <summary>
/// Background service that polls for events and routes them to handlers
/// </summary>
public sealed class SubscriptionPollingService(
    string moduleKey,
    EventRouter eventRouter,
    PollingOptions options,
    IServiceProvider serviceProvider,
    ILogger<SubscriptionPollingService> logger,
    IMetricsRecorder metrics)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting subscription polling service");

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

                    var allSuccessful = true;
                    foreach (var evt in events.OrderBy(e => e.GlobalPosition))
                    {
                        var success = await eventRouter.RouteEvent(evt, stoppingToken);
                        if (!success)
                        {
                            allSuccessful = false;
                            logger.LogError(
                                "Event routing failed for event {EventId} at position {Position}",
                                evt.Id,
                                evt.GlobalPosition
                            );
                            // Stop processing this batch if we hit a poison pill
                            break;
                        }
                    }

                    if (!allSuccessful)
                    {
                        logger.LogWarning("Stopping polling due to subscription failures");
                        break;
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

        logger.LogInformation("Subscription polling service stopped");
    }
}