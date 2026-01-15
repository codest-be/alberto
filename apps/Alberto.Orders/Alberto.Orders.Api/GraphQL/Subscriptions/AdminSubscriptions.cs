using System.Runtime.CompilerServices;
using Alberto.Dcb.Admin.Subscriptions;
using Alberto.Orders.Api.GraphQL.Types;

namespace Alberto.Orders.Api.GraphQL.Subscriptions;

/// <summary>
/// GraphQL subscriptions for admin monitoring.
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

    public static async IAsyncEnumerable<ProcessorStatusUpdated> SubscribeToProcessorStatus(
        [Service] InMemoryAdminPublisher publisher,
        string? moduleKey = null,
        string? processorId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in publisher.ProcessorUpdates.ReadAllAsync(ct))
        {
            if (moduleKey is not null && update.ModuleKey != moduleKey)
                continue;
            if (processorId is not null && update.Status.ProcessorId != processorId)
                continue;

            yield return new ProcessorStatusUpdated(
                update.ModuleKey,
                ProcessorStatus.FromDto(update.Status));
        }
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

    public static async IAsyncEnumerable<CheckpointUpdated> SubscribeToCheckpoints(
        [Service] InMemoryAdminPublisher publisher,
        string? moduleKey = null,
        string? processorId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in publisher.CheckpointUpdates.ReadAllAsync(ct))
        {
            if (moduleKey is not null && update.ModuleKey != moduleKey)
                continue;
            if (processorId is not null && update.Checkpoint.ProcessorId != processorId)
                continue;

            yield return new CheckpointUpdated(
                update.ModuleKey,
                Checkpoint.FromDto(update.Checkpoint));
        }
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

    public static async IAsyncEnumerable<DeadLetterChanged> SubscribeToDeadLetters(
        [Service] InMemoryAdminPublisher publisher,
        string? moduleKey = null,
        string? processorId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in publisher.DeadLetterUpdates.ReadAllAsync(ct))
        {
            if (moduleKey is not null && update.ModuleKey != moduleKey)
                continue;
            if (processorId is not null && update.DeadLetter.ProcessorId != processorId)
                continue;

            yield return new DeadLetterChanged(
                update.ModuleKey,
                DeadLetter.FromDto(update.DeadLetter),
                update.ChangeType.ToString());
        }
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

    public static async IAsyncEnumerable<SystemInfoUpdated> SubscribeToSystemInfo(
        [Service] InMemoryAdminPublisher publisher,
        string? moduleKey = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in publisher.SystemInfoUpdates.ReadAllAsync(ct))
        {
            if (moduleKey is not null && update.ModuleKey != moduleKey)
                continue;

            yield return new SystemInfoUpdated(
                update.ModuleKey,
                SystemInfo.FromDto(update.Info));
        }
    }

    #endregion
}
