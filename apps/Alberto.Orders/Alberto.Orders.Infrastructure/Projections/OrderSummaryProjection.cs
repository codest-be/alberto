using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure.ReadModels;

namespace Alberto.Orders.Infrastructure.Projections;

/// <summary>
/// Projection that builds OrderSummary read models from order events.
/// </summary>
public sealed class OrderSummaryProjection : Projection<OrderSummary>,
    IProject<OrderSummary, OrderCreated>,
    IProject<OrderSummary, OrderItemAdded>,
    IProject<OrderSummary, OrderItemRemoved>,
    IProject<OrderSummary, OrderConfirmed>,
    IProject<OrderSummary, OrderShipped>,
    IProject<OrderSummary, OrderDelivered>,
    IProject<OrderSummary, OrderCancelled>
{
    public string GetDocumentId(OrderCreated e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderCreated e, ProjectionContext ctx)
    {
        return new OrderSummary
        {
            OrderId = e.OrderId,
            CustomerId = e.CustomerId,
            LineItems = e.LineItems.Select(ToSummary).ToList(),
            Notes = e.Notes,
            Status = OrderStatus.Draft,
            Total = e.LineItems.Sum(x => x.Total),
            CreatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderItemAdded e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderItemAdded e, ProjectionContext ctx)
    {
        var items = state.LineItems.Where(x => x.ProductId != e.ProductId).ToList();
        items.Add(new OrderLineItemSummary
        {
            ProductId = e.ProductId,
            ProductName = e.ProductName,
            Quantity = e.Quantity,
            UnitPrice = e.UnitPrice,
            Total = e.Quantity * e.UnitPrice
        });

        return state with
        {
            LineItems = items,
            Total = items.Sum(x => x.Total),
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderItemRemoved e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderItemRemoved e, ProjectionContext ctx)
    {
        var items = state.LineItems.Where(x => x.ProductId != e.ProductId).ToList();

        return state with
        {
            LineItems = items,
            Total = items.Sum(x => x.Total),
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderConfirmed e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderConfirmed e, ProjectionContext ctx)
    {
        return state with
        {
            Status = OrderStatus.Confirmed,
            ConfirmedAt = e.ConfirmedAt,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderShipped e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderShipped e, ProjectionContext ctx)
    {
        return state with
        {
            Status = OrderStatus.Shipped,
            TrackingNumber = e.TrackingNumber,
            Carrier = e.Carrier,
            ShippedAt = e.ShippedAt,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderDelivered e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderDelivered e, ProjectionContext ctx)
    {
        return state with
        {
            Status = OrderStatus.Delivered,
            DeliveredAt = e.DeliveredAt,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderCancelled e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderCancelled e, ProjectionContext ctx)
    {
        return state with
        {
            Status = OrderStatus.Cancelled,
            CancellationReason = e.Reason,
            CancelledAt = e.CancelledAt,
            UpdatedAt = ctx.Timestamp
        };
    }

    private static OrderLineItemSummary ToSummary(OrderLineItem item) => new()
    {
        ProductId = item.ProductId,
        ProductName = item.ProductName,
        Quantity = item.Quantity,
        UnitPrice = item.UnitPrice,
        Total = item.Total
    };
}
