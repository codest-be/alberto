using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Interface for publishing processor status updates.
/// </summary>
public interface IProcessorStatusPublisher
{
    /// <summary>
    /// Publishes a processor status update.
    /// </summary>
    Task PublishAsync(string moduleKey, ProcessorStatusDto status, CancellationToken ct = default);
}
