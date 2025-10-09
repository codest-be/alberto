using Alberto.EventSourcing;

namespace Alberto.Example.Modules.Orders;

/// <summary>
/// Centralized problem definitions for order operations
/// </summary>
public static class OrderProblems
{
    public static Problem OrderNotFound(Guid orderId) =>
        Problem.Create("ORDER_NOT_FOUND", $"Order {orderId} does not exist");

    public static Problem OrderAlreadyCancelled() =>
        Problem.Create("ORDER_ALREADY_CANCELLED", "Order is already cancelled");

    public static Problem CannotCancelShippedOrder() =>
        Problem.Create("CANNOT_CANCEL_SHIPPED_ORDER", "Cannot cancel an order that has already been shipped");

    public static Problem InvalidStatusForPlacing(OrderStatus currentStatus) =>
        Problem.Create("INVALID_ORDER_STATUS",
            $"Order must be in Created status to be placed. Current status: {currentStatus}");

    public static Problem InvalidStatusForShipping(OrderStatus currentStatus) =>
        Problem.Create("INVALID_ORDER_STATUS",
            $"Order must be in Placed status to be shipped. Current status: {currentStatus}");
}