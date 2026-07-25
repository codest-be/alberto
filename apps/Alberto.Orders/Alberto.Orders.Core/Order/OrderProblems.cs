using Alberto.Dcb;

namespace Alberto.Orders.Core.Order;

/// <summary>
/// The failure taxonomy for order decisions. Codes follow <c>order.{kebab-reason}</c>
/// and are the contract the API layer maps to typed errors — callers match on
/// <see cref="Problem.Code"/>, never on message text.
/// </summary>
public static class OrderProblems
{
    public static Problem NotFound() =>
        Problem.Create("order.not-found", "Order does not exist");

    public static Problem AlreadyExists(Guid orderId) =>
        Problem.Create(
            "order.already-exists",
            $"Order {orderId} already exists",
            new Dictionary<string, object> { ["orderId"] = orderId });

    /// <summary>
    /// The order is in a status that does not permit <paramref name="operation"/>.
    /// One code, with the attempted operation and the blocking status in the details,
    /// so consumers can branch without parsing the message.
    /// </summary>
    public static Problem InvalidStatus(string operation, OrderStatus status) =>
        Problem.Create(
            "order.invalid-status",
            $"Order cannot be {operation} in {status} status",
            new Dictionary<string, object>
            {
                ["operation"] = operation,
                ["status"] = status.ToString()
            });

    public static Problem Empty() =>
        Problem.Create("order.empty", "Cannot confirm an empty order");

    public static Problem ProductNotFound(Guid productId) =>
        Problem.Create(
            "order.product-not-found",
            $"Product {productId} not found in order",
            new Dictionary<string, object> { ["productId"] = productId });

    public static Problem CustomerRequired() =>
        Problem.Create("order.customer-required", "Customer ID is required");

    public static Problem InvalidQuantity() =>
        Problem.Create("order.invalid-quantity", "Quantity must be greater than zero");

    public static Problem InvalidUnitPrice() =>
        Problem.Create("order.invalid-unit-price", "Unit price cannot be negative");

    public static Problem CancellationReasonRequired() =>
        Problem.Create("order.cancellation-reason-required", "Cancellation reason is required");

    public static Problem TrackingNumberRequired() =>
        Problem.Create("order.tracking-number-required", "Tracking number is required");

    public static Problem CarrierRequired() =>
        Problem.Create("order.carrier-required", "Carrier is required");
}
