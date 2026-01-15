using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Legacy processor status publisher - delegates to InMemoryAdminPublisher.
/// </summary>
public sealed class InMemoryProcessorStatusPublisher : IProcessorStatusPublisher
{
    private readonly IAdminPublisher _publisher;

    public InMemoryProcessorStatusPublisher(IAdminPublisher publisher)
    {
        _publisher = publisher;
    }

    public Task PublishAsync(string moduleKey, ProcessorStatusDto status, CancellationToken ct = default)
    {
        return _publisher.PublishProcessorAsync(moduleKey, status, ct);
    }
}
