using Alberto.Dcb.Admin.Subscriptions;
using Alberto.Orders.Api.GraphQL.Types;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;

namespace Alberto.Orders.Api.GraphQL.Subscriptions;

/// <summary>
/// GraphQL subscriptions for admin monitoring using HotChocolate's topic-based pub/sub.
/// </summary>
[SubscriptionType]
public static class AdminSubscriptions
{
    #region Processor Status

    /// <summary>
    /// Subscribes to real-time processor status updates.
    /// </summary>
    [Subscribe(With = nameof(SubscribeToProcessorStatus))]
    [GraphQLDescription("Subscribes to real-time processor status updates.")]
    public static ProcessorStatusUpdated OnProcessorStatusUpdated(
        [EventMessage] ProcessorStatusUpdated update) => update;

    public static async ValueTask<ISourceStream<ProcessorStatusUpdated>> SubscribeToProcessorStatus(
        string? moduleKey,
        string? processorId,
        [Service] ITopicEventReceiver eventReceiver,
        CancellationToken ct)
    {
        var sourceStream = await eventReceiver.SubscribeAsync<ProcessorStatusUpdate>(AdminTopics.ProcessorStatus, ct);

        return new FilteredSourceStream<ProcessorStatusUpdate, ProcessorStatusUpdated>(
            sourceStream,
            update =>
            {
                if (moduleKey is not null && update.ModuleKey != moduleKey)
                    return null;
                if (processorId is not null && update.Status.ProcessorId != processorId)
                    return null;

                return new ProcessorStatusUpdated(
                    update.ModuleKey,
                    ProcessorStatus.FromDto(update.Status));
            });
    }

    #endregion

    #region Checkpoints

    /// <summary>
    /// Subscribes to real-time checkpoint updates.
    /// </summary>
    [Subscribe(With = nameof(SubscribeToCheckpoints))]
    [GraphQLDescription("Subscribes to real-time checkpoint updates.")]
    public static CheckpointUpdated OnCheckpointUpdated(
        [EventMessage] CheckpointUpdated update) => update;

    public static async ValueTask<ISourceStream<CheckpointUpdated>> SubscribeToCheckpoints(
        string? moduleKey,
        string? processorId,
        [Service] ITopicEventReceiver eventReceiver,
        CancellationToken ct)
    {
        var sourceStream = await eventReceiver.SubscribeAsync<CheckpointUpdate>(AdminTopics.Checkpoint, ct);

        return new FilteredSourceStream<CheckpointUpdate, CheckpointUpdated>(
            sourceStream,
            update =>
            {
                if (moduleKey is not null && update.ModuleKey != moduleKey)
                    return null;
                if (processorId is not null && update.Checkpoint.ProcessorId != processorId)
                    return null;

                return new CheckpointUpdated(
                    update.ModuleKey,
                    Checkpoint.FromDto(update.Checkpoint));
            });
    }

    #endregion

    #region Dead Letters

    /// <summary>
    /// Subscribes to real-time dead letter changes.
    /// </summary>
    [Subscribe(With = nameof(SubscribeToDeadLetters))]
    [GraphQLDescription("Subscribes to real-time dead letter changes (new failures).")]
    public static DeadLetterChanged OnDeadLetterChanged(
        [EventMessage] DeadLetterChanged update) => update;

    public static async ValueTask<ISourceStream<DeadLetterChanged>> SubscribeToDeadLetters(
        string? moduleKey,
        string? processorId,
        [Service] ITopicEventReceiver eventReceiver,
        CancellationToken ct)
    {
        var sourceStream = await eventReceiver.SubscribeAsync<DeadLetterUpdate>(AdminTopics.DeadLetter, ct);

        return new FilteredSourceStream<DeadLetterUpdate, DeadLetterChanged>(
            sourceStream,
            update =>
            {
                if (moduleKey is not null && update.ModuleKey != moduleKey)
                    return null;
                if (processorId is not null && update.DeadLetter.ProcessorId != processorId)
                    return null;

                return new DeadLetterChanged(
                    update.ModuleKey,
                    DeadLetter.FromDto(update.DeadLetter),
                    update.ChangeType.ToString());
            });
    }

    #endregion

    #region System Info

    /// <summary>
    /// Subscribes to real-time system info updates.
    /// </summary>
    [Subscribe(With = nameof(SubscribeToSystemInfo))]
    [GraphQLDescription("Subscribes to real-time system info updates.")]
    public static SystemInfoUpdated OnSystemInfoUpdated(
        [EventMessage] SystemInfoUpdated update) => update;

    public static async ValueTask<ISourceStream<SystemInfoUpdated>> SubscribeToSystemInfo(
        string? moduleKey,
        [Service] ITopicEventReceiver eventReceiver,
        CancellationToken ct)
    {
        var sourceStream = await eventReceiver.SubscribeAsync<SystemInfoUpdate>(AdminTopics.SystemInfo, ct);

        return new FilteredSourceStream<SystemInfoUpdate, SystemInfoUpdated>(
            sourceStream,
            update =>
            {
                if (moduleKey is not null && update.ModuleKey != moduleKey)
                    return null;

                return new SystemInfoUpdated(
                    update.ModuleKey,
                    SystemInfo.FromDto(update.Info));
            });
    }

    #endregion
}

/// <summary>
/// A source stream that filters and transforms messages, skipping nulls.
/// </summary>
internal sealed class FilteredSourceStream<TSource, TResult> : ISourceStream<TResult>
    where TResult : class
{
    private readonly ISourceStream<TSource> _inner;
    private readonly Func<TSource, TResult?> _transform;

    public FilteredSourceStream(ISourceStream<TSource> inner, Func<TSource, TResult?> transform)
    {
        _inner = inner;
        _transform = transform;
    }

    public IAsyncEnumerable<TResult> ReadEventsAsync() => FilteredEnumerable();

    IAsyncEnumerable<object?> ISourceStream.ReadEventsAsync() => FilteredEnumerableBoxed();

    private async IAsyncEnumerable<TResult> FilteredEnumerable()
    {
        await foreach (var item in _inner.ReadEventsAsync())
        {
            var result = _transform(item);
            if (result is not null)
            {
                yield return result;
            }
        }
    }

    private async IAsyncEnumerable<object?> FilteredEnumerableBoxed()
    {
        await foreach (var item in FilteredEnumerable())
        {
            yield return item;
        }
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
