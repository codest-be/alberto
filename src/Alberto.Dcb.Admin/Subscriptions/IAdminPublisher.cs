using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Interface for publishing admin updates via HotChocolate's topic-based pub/sub.
/// </summary>
public interface IAdminPublisher
{
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
