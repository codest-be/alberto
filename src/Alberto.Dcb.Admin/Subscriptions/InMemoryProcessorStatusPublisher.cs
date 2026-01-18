using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Legacy processor status publisher - delegates to InMemoryAdminPublisher.
/// </summary>
internal sealed class InMemoryProcessorStatusPublisher(IAdminPublisher publisher) : IProcessorStatusPublisher
{
    public Task PublishAsync(string moduleKey, ProcessorStatusDto status, CancellationToken ct = default)
    {
        return publisher.PublishProcessorAsync(moduleKey, status, ct);
    }
}
