using System.Threading.Channels;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Subscriptions.Polling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Channel;

/// <summary>
/// Background service that consumes events from a channel and routes them to handlers
/// </summary>
public sealed class ChannelSubscriptionService(
    string moduleKey,
    EventRouter eventRouter,
    ChannelReader<GlobalEventEnvelope> channelReader,
    ChannelOptions options,
    IEnumerable<IChannelConsumer> consumers,
    IMetricsRecorder metrics,
    ILogger<ChannelSubscriptionService> logger)
    : BackgroundService
{
    private const double HighWaterMarkPercentage = 0.8; // 80% capacity threshold
    private readonly List<IChannelConsumer> _consumers = consumers.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Starting channel subscription service for module '{ModuleKey}' with capacity {Capacity}",
            moduleKey,
            options.BoundedCapacity
        );

        // Initialize handler checkpoints
        await eventRouter.InitializeHandlers(stoppingToken);

        // Start channel depth monitoring task
        var monitoringTask = MonitorChannelDepth(stoppingToken);

        try
        {
            // Read events from channel
            await foreach (var evt in channelReader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // Route to event handlers
                    var handlerSuccess = await eventRouter.RouteEvent(evt, stoppingToken);

                    if (!handlerSuccess)
                    {
                        logger.LogWarning(
                            "Handler processing failed for event {EventId} at position {Position}",
                            evt.Id,
                            evt.GlobalPosition
                        );
                        // Continue processing other events even if one handler fails
                    }

                    // Notify channel consumers in parallel
                    if (_consumers.Count > 0)
                    {
                        await NotifyConsumers(evt, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Error processing event {EventId} at position {Position}",
                        evt.Id,
                        evt.GlobalPosition
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in channel subscription service");
        }

        // Wait for monitoring task to complete
        try
        {
            await monitoringTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        logger.LogInformation("Channel subscription service stopped for module '{ModuleKey}'", moduleKey);
    }

    private async Task NotifyConsumers(GlobalEventEnvelope evt, CancellationToken cancellationToken)
    {
        if (options.AllowParallelExecution)
        {
            // Execute consumers in parallel
            var tasks = _consumers.Select(c => SafeConsumeAsync(c, evt, cancellationToken));
            await Task.WhenAll(tasks);
        }
        else
        {
            // Execute consumers sequentially
            foreach (var consumer in _consumers)
            {
                await SafeConsumeAsync(consumer, evt, cancellationToken);
            }
        }
    }

    private async Task SafeConsumeAsync(
        IChannelConsumer consumer,
        GlobalEventEnvelope evt,
        CancellationToken cancellationToken)
    {
        try
        {
            await consumer.Consume(evt, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Error in channel consumer {ConsumerType} for event {EventId}",
                consumer.GetType().Name,
                evt.Id
            );
        }
    }

    private async Task MonitorChannelDepth(CancellationToken cancellationToken)
    {
        var highWaterMarkThreshold = (int)(options.BoundedCapacity * HighWaterMarkPercentage);
        var hasLoggedWarning = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

                // Try to get channel depth if available
                var depth = GetChannelDepth();
                if (depth.HasValue)
                {
                    // Update metrics
                    metrics.UpdateChannelDepth(moduleKey, depth.Value);

                    // Log warning if depth exceeds high water mark
                    if (depth.Value >= highWaterMarkThreshold)
                    {
                        if (!hasLoggedWarning)
                        {
                            logger.LogWarning(
                                "Channel depth for module '{ModuleKey}' has exceeded {Percentage}% capacity: {Depth}/{Capacity} events. " +
                                "Consider increasing BoundedCapacity or optimizing event handlers.",
                                moduleKey,
                                (int)(HighWaterMarkPercentage * 100),
                                depth.Value,
                                options.BoundedCapacity
                            );
                            hasLoggedWarning = true;
                        }
                    }
                    else
                    {
                        // Reset warning flag when depth drops below threshold
                        hasLoggedWarning = false;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when service is stopping
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in channel depth monitoring for module '{ModuleKey}'", moduleKey);
        }
    }

    private int? GetChannelDepth()
    {
        // Try to get Count property via reflection if available
        // BoundedChannel<T> exposes Count property, but ChannelReader<T> doesn't
        try
        {
            var countProperty = channelReader.GetType().GetProperty("Count");
            if (countProperty != null)
            {
                return (int?)countProperty.GetValue(channelReader);
            }
        }
        catch
        {
            // Ignore reflection errors
        }

        return null;
    }
}