using Alberto.Dcb;

namespace Alberto.Orders.Core.Order;

/// <summary>
/// Decider for order operations. Contains business logic as pure functions.
/// </summary>
public sealed partial class OrderDecider
{
    /// <summary>
    /// Gets the DCB query for an order's consistency boundary.
    /// </summary>
    public static DcbQuery BoundaryFor(Guid orderId) =>
        DcbQuery.For(Tags.Order, orderId);
}
