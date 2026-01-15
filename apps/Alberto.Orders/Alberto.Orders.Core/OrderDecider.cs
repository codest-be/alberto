using Alberto.Dcb;

namespace Alberto.Orders.Core;

/// <summary>
/// Decider for order operations. Contains business logic as pure functions.
/// </summary>
public sealed class OrderDecider
{
    #region State Transitions (Apply)

    public OrderState Apply(OrderState state, OrderCreated e) => state with
    {
        OrderId = e.OrderId,
        CustomerId = e.CustomerId,
        LineItems = e.LineItems,
        Notes = e.Notes,
        Status = OrderStatus.Draft
    };

    public OrderState Apply(OrderState state, OrderItemAdded e) => state with
    {
        LineItems = state.LineItems
            .Where(x => x.ProductId != e.ProductId)
            .Append(new OrderLineItem(e.ProductId, e.ProductName, e.Quantity, e.UnitPrice))
            .ToList()
    };

    public OrderState Apply(OrderState state, OrderItemRemoved e) => state with
    {
        LineItems = state.LineItems.Where(x => x.ProductId != e.ProductId).ToList()
    };

    public OrderState Apply(OrderState state, OrderConfirmed e) => state with
    {
        Status = OrderStatus.Confirmed,
        ConfirmedAt = e.ConfirmedAt
    };

    public OrderState Apply(OrderState state, OrderShipped e) => state with
    {
        Status = OrderStatus.Shipped,
        TrackingNumber = e.TrackingNumber,
        Carrier = e.Carrier,
        ShippedAt = e.ShippedAt
    };

    public OrderState Apply(OrderState state, OrderDelivered e) => state with
    {
        Status = OrderStatus.Delivered,
        DeliveredAt = e.DeliveredAt
    };

    public OrderState Apply(OrderState state, OrderCancelled e) => state with
    {
        Status = OrderStatus.Cancelled,
        CancellationReason = e.Reason,
        CancelledAt = e.CancelledAt
    };

    #endregion

    #region Decision Methods

    /// <summary>
    /// Creates a new order.
    /// </summary>
    public static DecisionResult Create(
        OrderState state,
        Guid orderId,
        Guid customerId,
        IReadOnlyList<OrderLineItem> lineItems,
        string? notes = null)
    {
        if (state.Exists)
            return DecisionResult.Fail($"Order {orderId} already exists");

        if (customerId == Guid.Empty)
            return DecisionResult.Fail("Customer ID is required");

        return DecisionResult.Ok(new OrderCreated(orderId, customerId, lineItems, notes));
    }

    /// <summary>
    /// Adds an item to the order.
    /// </summary>
    public static DecisionResult AddItem(
        OrderState state,
        Guid productId,
        string productName,
        int quantity,
        decimal unitPrice)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeModified)
            return DecisionResult.Fail($"Order cannot be modified in {state.Status} status");

        if (quantity <= 0)
            return DecisionResult.Fail("Quantity must be greater than zero");

        if (unitPrice < 0)
            return DecisionResult.Fail("Unit price cannot be negative");

        return DecisionResult.Ok(new OrderItemAdded(state.OrderId, productId, productName, quantity, unitPrice));
    }

    /// <summary>
    /// Removes an item from the order.
    /// </summary>
    public static DecisionResult RemoveItem(OrderState state, Guid productId)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeModified)
            return DecisionResult.Fail($"Order cannot be modified in {state.Status} status");

        if (state.LineItems.All(x => x.ProductId != productId))
            return DecisionResult.Fail($"Product {productId} not found in order");

        return DecisionResult.Ok(new OrderItemRemoved(state.OrderId, productId));
    }

    /// <summary>
    /// Confirms the order for processing.
    /// </summary>
    public static DecisionResult Confirm(OrderState state, DateTimeOffset confirmedAt)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeConfirmed)
            return DecisionResult.Fail(state.LineItems.Count == 0
                ? "Cannot confirm an empty order"
                : $"Order cannot be confirmed in {state.Status} status");

        return DecisionResult.Ok(new OrderConfirmed(state.OrderId, confirmedAt));
    }

    /// <summary>
    /// Ships the order.
    /// </summary>
    public static DecisionResult Ship(
        OrderState state,
        string trackingNumber,
        string carrier,
        DateTimeOffset shippedAt)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeShipped)
            return DecisionResult.Fail($"Order cannot be shipped in {state.Status} status");

        if (string.IsNullOrWhiteSpace(trackingNumber))
            return DecisionResult.Fail("Tracking number is required");

        if (string.IsNullOrWhiteSpace(carrier))
            return DecisionResult.Fail("Carrier is required");

        return DecisionResult.Ok(new OrderShipped(state.OrderId, trackingNumber, carrier, shippedAt));
    }

    /// <summary>
    /// Marks the order as delivered.
    /// </summary>
    public static DecisionResult Deliver(OrderState state, DateTimeOffset deliveredAt)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeDelivered)
            return DecisionResult.Fail($"Order cannot be delivered in {state.Status} status");

        return DecisionResult.Ok(new OrderDelivered(state.OrderId, deliveredAt));
    }

    /// <summary>
    /// Cancels the order.
    /// </summary>
    public static DecisionResult Cancel(OrderState state, string reason, DateTimeOffset cancelledAt)
    {
        if (!state.Exists)
            return DecisionResult.Fail("Order does not exist");

        if (!state.CanBeCancelled)
            return DecisionResult.Fail($"Order cannot be cancelled in {state.Status} status");

        if (string.IsNullOrWhiteSpace(reason))
            return DecisionResult.Fail("Cancellation reason is required");

        return DecisionResult.Ok(new OrderCancelled(state.OrderId, reason, cancelledAt));
    }

    #endregion

    #region Stream Boundary

    /// <summary>
    /// Gets the DCB query for an order's consistency boundary.
    /// </summary>
    public static DcbQuery BoundaryFor(Guid orderId) =>
        DcbQuery.Empty.WithTag(Tags.Order, orderId.ToString());

    #endregion
}

/// <summary>
/// Result of a decision operation.
/// </summary>
public readonly struct DecisionResult
{
    public bool IsSuccess { get; }
    public IEvent? Event { get; }
    public string? Error { get; }

    private DecisionResult(bool isSuccess, IEvent? @event, string? error)
    {
        IsSuccess = isSuccess;
        Event = @event;
        Error = error;
    }

    public static DecisionResult Ok(IEvent @event) => new(true, @event, null);
    public static DecisionResult Fail(string error) => new(false, null, error);

    public void EnsureSuccess()
    {
        if (!IsSuccess)
            throw new InvalidOperationException(Error);
    }
}
