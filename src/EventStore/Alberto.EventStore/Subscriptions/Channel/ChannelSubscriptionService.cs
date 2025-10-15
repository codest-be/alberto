using System.Threading.Channels;
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
    ILogger<ChannelSubscriptionService> logger)
    : BackgroundService
{
    private readonly List<IChannelConsumer> _consumers = consumers.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Starting channel subscription service for module '{ModuleKey}' in {Mode} mode",
            moduleKey,
            options.Mode
        );

        // Initialize handler checkpoints
        await eventRouter.InitializeHandlers(stoppingToken);

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
}