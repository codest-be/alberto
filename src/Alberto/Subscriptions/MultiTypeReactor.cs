using Alberto.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Subscriptions;

/// <summary>
/// A reactor that handles several event types under one processor id, one checkpoint and one lease,
/// in log order. Used by <c>React&lt;THandler&gt;(...)</c>.
/// </summary>
internal sealed class MultiTypeReactor<THandler>(
    string processorId,
    IServiceScopeFactory scopeFactory,
    IReadOnlyDictionary<string, Func<THandler, IEventEnvelope, EventSerializer?, ReactorContext, CancellationToken, Task>> handlers,
    int maxConcurrency = 1,
    EventSerializer? serializer = null) : IBatchableProcessor, IProcessorLifecycle
    where THandler : class
{
    public string ProcessorId { get; } = processorId;

    public bool IsActive { get; set; } = true;

    public bool IsRebuilding { get; set; }

    public IReadOnlySet<string> HandledEventTypes { get; } =
        new HashSet<string>(handlers.Keys, StringComparer.Ordinal);

    public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default)
    {
        if (!IsActive || !HandledEventTypes.Contains(@event.EventType.Id))
            return Task.CompletedTask;

        return ProcessAsync(@event, cancellationToken);
    }

    public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default)
    {
        if (!IsActive)
            return;

        if (maxConcurrency <= 1)
        {
            foreach (var @event in events)
                await ProcessAsync(@event, cancellationToken);
            return;
        }

        await Parallel.ForEachAsync(
            events,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
            async (@event, token) => await ProcessAsync(@event, token));
    }

    private async Task ProcessAsync(IEventEnvelope @event, CancellationToken cancellationToken)
    {
        var context = ReactorContext.FromEnvelope(@event);
        await using var scope = EventProcessingScope.Create(scopeFactory, context.TenantId);
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        await handlers[@event.EventType.Id](handler, @event, serializer, context, cancellationToken);
    }
}
