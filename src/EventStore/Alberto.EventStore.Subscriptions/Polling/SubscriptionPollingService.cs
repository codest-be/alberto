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
    ILogger<SubscriptionPollingService> logger)
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
                await using var scope = serviceProvider.CreateAsyncScope();
                var fromPosition = eventRouter.GetMinimumPosition();

                var events = await scope.ServiceProvider.GetRequiredKeyedService<IMultiTenantEventStore>(moduleKey)
                    .StreamAll(
                        fromPosition,
                        options.MaxPageSize,
                        eventTypes: null, // Get all event types
                        stoppingToken
                    );

                if (events.Count > 0)
                {
                    logger.LogDebug(
                        "Retrieved {EventCount} events from position {FromPosition}",
                        events.Count,
                        fromPosition
                    );

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

/// <summary>
/// Options for polling configuration
/// </summary>
public sealed class PollingOptions
{
    public int MinPollingIntervalMs { get; set; } = 100;
    public int MaxPollingIntervalMs { get; set; } = 5000;
    public double PollingGrowFactor { get; set; } = 1.5;
    public int MaxPageSize { get; set; } = 100;
    public int? GapAgeThresholdMs { get; set; } = 60000;
    public int GapSkipTimeoutMs { get; set; } = 5000;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
}