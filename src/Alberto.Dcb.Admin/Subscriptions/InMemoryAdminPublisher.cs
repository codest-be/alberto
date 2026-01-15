using Alberto.Dcb.Admin.Api.Models;
using HotChocolate.Subscriptions;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Topic names for admin subscriptions.
/// </summary>
public static class AdminTopics
{
    public const string ProcessorStatus = "AdminProcessorStatus";
    public const string Checkpoint = "AdminCheckpoint";
    public const string DeadLetter = "AdminDeadLetter";
    public const string SystemInfo = "AdminSystemInfo";
}

/// <summary>
/// HotChocolate-based admin publisher that uses topic-based pub/sub for proper broadcast to all subscribers.
/// </summary>
public sealed class HotChocolateAdminPublisher : IAdminPublisher
{
    private readonly ITopicEventSender _eventSender;

    public HotChocolateAdminPublisher(ITopicEventSender eventSender)
    {
        _eventSender = eventSender;
    }

    public async Task PublishProcessorAsync(string moduleKey, ProcessorStatusDto status, CancellationToken ct = default)
    {
        await _eventSender.SendAsync(AdminTopics.ProcessorStatus, new ProcessorStatusUpdate(moduleKey, status), ct);
    }

    public async Task PublishCheckpointAsync(string moduleKey, CheckpointDto checkpoint, CancellationToken ct = default)
    {
        await _eventSender.SendAsync(AdminTopics.Checkpoint, new CheckpointUpdate(moduleKey, checkpoint), ct);
    }

    public async Task PublishDeadLetterAsync(string moduleKey, DeadLetterDto deadLetter, DeadLetterChangeType changeType, CancellationToken ct = default)
    {
        await _eventSender.SendAsync(AdminTopics.DeadLetter, new DeadLetterUpdate(moduleKey, deadLetter, changeType), ct);
    }

    public async Task PublishSystemInfoAsync(string moduleKey, SystemInfoDto info, CancellationToken ct = default)
    {
        await _eventSender.SendAsync(AdminTopics.SystemInfo, new SystemInfoUpdate(moduleKey, info), ct);
    }
}
