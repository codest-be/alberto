using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.EventStore.Subscriptions.Filters;

/// <summary>
/// Filter that intercepts event processing
/// </summary>
public interface IConsumeFilter
{
    /// <summary>
    /// Executes the filter
    /// </summary>
    ValueTask Execute(
        object @event,
        EventContext context,
        Func<ValueTask> next,
        CancellationToken cancellationToken = default
    );
}