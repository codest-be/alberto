using System.Threading.Channels;
using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// In-memory implementation of processor status publisher using channels.
/// </summary>
public sealed class InMemoryProcessorStatusPublisher : IProcessorStatusPublisher
{
    private readonly Channel<ProcessorStatusUpdate> _channel;

    public InMemoryProcessorStatusPublisher()
    {
        _channel = Channel.CreateBounded<ProcessorStatusUpdate>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });
    }

    public ChannelReader<ProcessorStatusUpdate> Reader => _channel.Reader;

    public async Task PublishAsync(string moduleKey, ProcessorStatusDto status, CancellationToken ct = default)
    {
        var update = new ProcessorStatusUpdate(moduleKey, status);
        await _channel.Writer.WriteAsync(update, ct);
    }
}

/// <summary>
/// Wrapper for processor status update with module context.
/// </summary>
public sealed record ProcessorStatusUpdate(string ModuleKey, ProcessorStatusDto Status);
