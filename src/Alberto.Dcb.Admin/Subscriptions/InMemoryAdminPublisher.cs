using System.Threading.Channels;
using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// In-memory implementation of admin publisher using channels.
/// </summary>
public sealed class InMemoryAdminPublisher : IAdminPublisher
{
    private readonly Channel<ProcessorStatusUpdate> _processorChannel;
    private readonly Channel<CheckpointUpdate> _checkpointChannel;
    private readonly Channel<DeadLetterUpdate> _deadLetterChannel;
    private readonly Channel<SystemInfoUpdate> _systemInfoChannel;

    public InMemoryAdminPublisher()
    {
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        };

        _processorChannel = Channel.CreateBounded<ProcessorStatusUpdate>(options);
        _checkpointChannel = Channel.CreateBounded<CheckpointUpdate>(options);
        _deadLetterChannel = Channel.CreateBounded<DeadLetterUpdate>(options);
        _systemInfoChannel = Channel.CreateBounded<SystemInfoUpdate>(options);
    }

    public ChannelReader<ProcessorStatusUpdate> ProcessorUpdates => _processorChannel.Reader;
    public ChannelReader<CheckpointUpdate> CheckpointUpdates => _checkpointChannel.Reader;
    public ChannelReader<DeadLetterUpdate> DeadLetterUpdates => _deadLetterChannel.Reader;
    public ChannelReader<SystemInfoUpdate> SystemInfoUpdates => _systemInfoChannel.Reader;

    public async Task PublishProcessorAsync(string moduleKey, ProcessorStatusDto status, CancellationToken ct = default)
    {
        await _processorChannel.Writer.WriteAsync(new ProcessorStatusUpdate(moduleKey, status), ct);
    }

    public async Task PublishCheckpointAsync(string moduleKey, CheckpointDto checkpoint, CancellationToken ct = default)
    {
        await _checkpointChannel.Writer.WriteAsync(new CheckpointUpdate(moduleKey, checkpoint), ct);
    }

    public async Task PublishDeadLetterAsync(string moduleKey, DeadLetterDto deadLetter, DeadLetterChangeType changeType, CancellationToken ct = default)
    {
        await _deadLetterChannel.Writer.WriteAsync(new DeadLetterUpdate(moduleKey, deadLetter, changeType), ct);
    }

    public async Task PublishSystemInfoAsync(string moduleKey, SystemInfoDto info, CancellationToken ct = default)
    {
        await _systemInfoChannel.Writer.WriteAsync(new SystemInfoUpdate(moduleKey, info), ct);
    }
}
