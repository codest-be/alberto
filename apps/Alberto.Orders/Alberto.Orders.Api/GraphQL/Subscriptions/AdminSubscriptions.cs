using Alberto.Dcb.Admin.Subscriptions;
using Alberto.Orders.Api.GraphQL.Types;

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
    [Subscribe]
    [Topic(AdminTopics.ProcessorStatus)]
    [GraphQLDescription("Subscribes to real-time processor status updates.")]
    public static ProcessorStatusUpdated OnProcessorStatusUpdated(
        string? moduleKey,
        string? processorId,
        [EventMessage] ProcessorStatusUpdate update)
    {
        // Filter by moduleKey and processorId if provided
        if (moduleKey is not null && update.ModuleKey != moduleKey)
            return null!;
        if (processorId is not null && update.Status.ProcessorId != processorId)
            return null!;

        return new ProcessorStatusUpdated(
            update.ModuleKey,
            ProcessorStatus.FromDto(update.Status));
    }

    #endregion

    #region Checkpoints

    /// <summary>
    /// Subscribes to real-time checkpoint updates.
    /// </summary>
    [Subscribe]
    [Topic(AdminTopics.Checkpoint)]
    [GraphQLDescription("Subscribes to real-time checkpoint updates.")]
    public static CheckpointUpdated OnCheckpointUpdated(
        string? moduleKey,
        string? processorId,
        [EventMessage] CheckpointUpdate update)
    {
        if (moduleKey is not null && update.ModuleKey != moduleKey)
            return null!;
        if (processorId is not null && update.Checkpoint.ProcessorId != processorId)
            return null!;

        return new CheckpointUpdated(
            update.ModuleKey,
            Checkpoint.FromDto(update.Checkpoint));
    }

    #endregion

    #region Dead Letters

    /// <summary>
    /// Subscribes to real-time dead letter changes.
    /// </summary>
    [Subscribe]
    [Topic(AdminTopics.DeadLetter)]
    [GraphQLDescription("Subscribes to real-time dead letter changes (new failures).")]
    public static DeadLetterChanged OnDeadLetterChanged(
        string? moduleKey,
        string? processorId,
        [EventMessage] DeadLetterUpdate update)
    {
        if (moduleKey is not null && update.ModuleKey != moduleKey)
            return null!;
        if (processorId is not null && update.DeadLetter.ProcessorId != processorId)
            return null!;

        return new DeadLetterChanged(
            update.ModuleKey,
            DeadLetter.FromDto(update.DeadLetter),
            update.ChangeType.ToString());
    }

    #endregion

    #region System Info

    /// <summary>
    /// Subscribes to real-time system info updates.
    /// </summary>
    [Subscribe]
    [Topic(AdminTopics.SystemInfo)]
    [GraphQLDescription("Subscribes to real-time system info updates.")]
    public static SystemInfoUpdated OnSystemInfoUpdated(
        string? moduleKey,
        [EventMessage] SystemInfoUpdate update)
    {
        if (moduleKey is not null && update.ModuleKey != moduleKey)
            return null!;

        return new SystemInfoUpdated(
            update.ModuleKey,
            SystemInfo.FromDto(update.Info));
    }

    #endregion
}
