using System.Threading.Channels;
using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Legacy processor status publisher - delegates to InMemoryAdminPublisher.
/// </summary>
public sealed class InMemoryProcessorStatusPublisher : IProcessorStatusPublisher
{
    private readonly InMemoryAdminPublisher _publisher;

    public InMemoryProcessorStatusPublisher(InMemoryAdminPublisher publisher)
    {
        _publisher = publisher;
    }

    public ChannelReader<ProcessorStatusUpdate> Reader => _publisher.ProcessorUpdates;

    public Task PublishAsync(string moduleKey, ProcessorStatusDto status, CancellationToken ct = default)
    {
        return _publisher.PublishProcessorAsync(moduleKey, status, ct);
    }
}
