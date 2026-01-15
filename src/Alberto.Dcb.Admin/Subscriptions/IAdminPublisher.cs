using System.Threading.Channels;
using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Interface for publishing admin updates.
/// </summary>
public interface IAdminPublisher
{
    /// <summary>
    /// Reader for processor status updates.
    /// </summary>
    ChannelReader<ProcessorStatusUpdate> ProcessorUpdates { get; }

    /// <summary>
    /// Reader for checkpoint updates.
    /// </summary>
    ChannelReader<CheckpointUpdate> CheckpointUpdates { get; }

    /// <summary>
    /// Reader for dead letter updates.
    /// </summary>
    ChannelReader<DeadLetterUpdate> DeadLetterUpdates { get; }

    /// <summary>
    /// Reader for system info updates.
    /// </summary>
    ChannelReader<SystemInfoUpdate> SystemInfoUpdates { get; }

    /// <summary>
    /// Publishes a processor status update.
    /// </summary>
    Task PublishProcessorAsync(string moduleKey, ProcessorStatusDto status, CancellationToken ct = default);

    /// <summary>
    /// Publishes a checkpoint update.
    /// </summary>
    Task PublishCheckpointAsync(string moduleKey, CheckpointDto checkpoint, CancellationToken ct = default);

    /// <summary>
    /// Publishes a dead letter update.
    /// </summary>
    Task PublishDeadLetterAsync(string moduleKey, DeadLetterDto deadLetter, DeadLetterChangeType changeType, CancellationToken ct = default);

    /// <summary>
    /// Publishes a system info update.
    /// </summary>
    Task PublishSystemInfoAsync(string moduleKey, SystemInfoDto info, CancellationToken ct = default);
}

/// <summary>
/// Processor status update event.
/// </summary>
public sealed record ProcessorStatusUpdate(string ModuleKey, ProcessorStatusDto Status);

/// <summary>
/// Checkpoint update event.
/// </summary>
public sealed record CheckpointUpdate(string ModuleKey, CheckpointDto Checkpoint);

/// <summary>
/// Dead letter update event.
/// </summary>
public sealed record DeadLetterUpdate(string ModuleKey, DeadLetterDto DeadLetter, DeadLetterChangeType ChangeType);

/// <summary>
/// System info update event.
/// </summary>
public sealed record SystemInfoUpdate(string ModuleKey, SystemInfoDto Info);

/// <summary>
/// Type of dead letter change.
/// </summary>
public enum DeadLetterChangeType
{
    Added,
    Removed
}
