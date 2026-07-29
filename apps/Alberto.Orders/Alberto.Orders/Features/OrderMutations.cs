using Alberto.Dcb;
using Microsoft.Extensions.DependencyInjection;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>
/// GraphQL mutations for order operations.
/// </summary>
/// <remarks>
/// Every mutation is one pipeline: <c>Handle</c> the input, <c>Load</c> the order's DCB
/// boundary (which folds the state and captures the position in one step), <c>Decide</c>
/// with a pure action, then <c>Commit</c> — appending under the same boundary the decision
/// was based on. Conflicts are retried by re-reading and re-deciding.
/// </remarks>
public static class OrderMutations
{
    private static readonly OrderEvolver _evolver = new();

    /// <summary>How many times a conflicted append is re-read and re-decided before giving up.</summary>
    private const int ConflictRetries = 3;

    /// <summary>
    /// Marks an order as delivered.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Marks a shipped order as delivered.")]
    public static async Task<MutationResult> DeliverOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(orderId)
            .Load(OrderDecider.BoundaryFor(orderId), _evolver)
            .Decide(state => OrderDecider.Deliver(state, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    /// <summary>
    /// Cancels an order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Cancels a draft or confirmed order.")]
    public static async Task<MutationResult> CancelOrder(
        CancelOrderInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await Store(sp)
            .Handle(input)
            .Load(cmd => OrderDecider.BoundaryFor(cmd.OrderId), _evolver)
            .Decide((cmd, state) => OrderDecider.Cancel(state, cmd.Reason, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }

    private static AlbertoStore Store(IServiceProvider sp) =>
        sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey);
}
