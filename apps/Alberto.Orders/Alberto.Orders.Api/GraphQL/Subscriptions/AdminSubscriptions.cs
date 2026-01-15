using System.Runtime.CompilerServices;
using Alberto.Dcb.Admin.Subscriptions;
using Alberto.Orders.Api.GraphQL.Types;

namespace Alberto.Orders.Api.GraphQL.Subscriptions;

/// <summary>
/// GraphQL subscriptions for admin monitoring.
/// </summary>
public static class AdminSubscriptions
{
    /// <summary>
    /// Subscribes to real-time processor status updates.
    /// </summary>
    [Subscribe]
    [GraphQLDescription("Subscribes to real-time processor status updates.")]
    public static async IAsyncEnumerable<ProcessorStatusUpdated> OnProcessorStatusUpdated(
        [Service] InMemoryProcessorStatusPublisher publisher,
        string? moduleKey = null,
        string? processorId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in publisher.Reader.ReadAllAsync(ct))
        {
            // Filter by module if specified
            if (moduleKey is not null && update.ModuleKey != moduleKey)
                continue;

            // Filter by processor if specified
            if (processorId is not null && update.Status.ProcessorId != processorId)
                continue;

            yield return new ProcessorStatusUpdated(
                update.ModuleKey,
                ProcessorStatus.FromDto(update.Status));
        }
    }
}
