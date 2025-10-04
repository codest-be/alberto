using Microsoft.Extensions.Logging;
using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.EventStore.Subscriptions.Filters;

/// <summary>
/// Pipeline that executes filters before/after event handling
/// </summary>
public sealed class ConsumePipeline(ILogger<ConsumePipeline> logger)
{
    private readonly List<IConsumeFilter> _filters = new();
    private readonly ILogger<ConsumePipeline> _logger = logger;

    public void AddFilter(IConsumeFilter filter)
    {
        _filters.Add(filter);
    }

    public async ValueTask Execute(
        object @event,
        EventContext context,
        Func<ValueTask> handler,
        CancellationToken cancellationToken)
    {
        var index = 0;

        async ValueTask ExecuteNext()
        {
            if (index < _filters.Count)
            {
                var filter = _filters[index++];
                await filter.Execute(@event, context, ExecuteNext, cancellationToken);
            }
            else
            {
                // Execute the actual handler
                await handler();
            }
        }

        await ExecuteNext();
    }
}