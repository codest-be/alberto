using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure.Entities;

namespace Alberto.Orders.Infrastructure.Projections;

/// <summary>
/// EF-based projection that builds OrderSummaryEntity read models from order events.
/// Uses proper relational columns instead of JSONB for rich SQL querying.
/// </summary>
public sealed class OrderSummaryEfProjection : Projection<OrderSummaryEntity>,
    IProject<OrderSummaryEntity, OrderCreated>,
    IProject<OrderSummaryEntity, OrderItemAdded>,
    IProject<OrderSummaryEntity, OrderItemRemoved>,
    IProject<OrderSummaryEntity, OrderConfirmed>,
    IProject<OrderSummaryEntity, OrderShipped>,
    IProject<OrderSummaryEntity, OrderDelivered>,
    IProject<OrderSummaryEntity, OrderCancelled>
{
    public string GetDocumentId(OrderCreated e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummaryEntity> Apply(OrderSummaryEntity state, OrderCreated e, ProjectionContext ctx)
    {
        return new OrderSummaryEntity
        {
            TenantId = ctx.TenantId,
            DocumentId = e.OrderId.ToString(),
            OrderId = e.OrderId,
            CustomerId = e.CustomerId,
            LineItems = e.LineItems.Select(li => new OrderLineItemData
            {
                ProductId = li.ProductId,
                ProductName = li.ProductName,
                Quantity = li.Quantity,
                UnitPrice = li.UnitPrice,
                Total = li.Total
            }).ToList(),
            Notes = e.Notes,
            Status = OrderStatus.Draft,
            Total = e.LineItems.Sum(x => x.Total),
            CreatedAt = ctx.Timestamp,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderItemAdded e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummaryEntity> Apply(OrderSummaryEntity state, OrderItemAdded e, ProjectionContext ctx)
    {
        // Remove existing item with same product (if any) and add new
        var items = state.LineItems.Where(x => x.ProductId != e.ProductId).ToList();
        items.Add(new OrderLineItemData
        {
            ProductId = e.ProductId,
            ProductName = e.ProductName,
            Quantity = e.Quantity,
            UnitPrice = e.UnitPrice,
            Total = e.Quantity * e.UnitPrice
        });

        return new OrderSummaryEntity
        {
            TenantId = state.TenantId,
            DocumentId = state.DocumentId,
            OrderId = state.OrderId,
            CustomerId = state.CustomerId,
            LineItems = items,
            Notes = state.Notes,
            Status = state.Status,
            Total = items.Sum(x => x.Total),
            CreatedAt = state.CreatedAt,
            ConfirmedAt = state.ConfirmedAt,
            ShippedAt = state.ShippedAt,
            DeliveredAt = state.DeliveredAt,
            CancelledAt = state.CancelledAt,
            TrackingNumber = state.TrackingNumber,
            Carrier = state.Carrier,
            CancellationReason = state.CancellationReason,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderItemRemoved e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummaryEntity> Apply(OrderSummaryEntity state, OrderItemRemoved e, ProjectionContext ctx)
    {
        var items = state.LineItems.Where(x => x.ProductId != e.ProductId).ToList();

        return new OrderSummaryEntity
        {
            TenantId = state.TenantId,
            DocumentId = state.DocumentId,
            OrderId = state.OrderId,
            CustomerId = state.CustomerId,
            LineItems = items,
            Notes = state.Notes,
            Status = state.Status,
            Total = items.Sum(x => x.Total),
            CreatedAt = state.CreatedAt,
            ConfirmedAt = state.ConfirmedAt,
            ShippedAt = state.ShippedAt,
            DeliveredAt = state.DeliveredAt,
            CancelledAt = state.CancelledAt,
            TrackingNumber = state.TrackingNumber,
            Carrier = state.Carrier,
            CancellationReason = state.CancellationReason,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderConfirmed e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummaryEntity> Apply(OrderSummaryEntity state, OrderConfirmed e, ProjectionContext ctx)
    {
        return new OrderSummaryEntity
        {
            TenantId = state.TenantId,
            DocumentId = state.DocumentId,
            OrderId = state.OrderId,
            CustomerId = state.CustomerId,
            LineItems = state.LineItems,
            Notes = state.Notes,
            Status = OrderStatus.Confirmed,
            Total = state.Total,
            CreatedAt = state.CreatedAt,
            ConfirmedAt = e.ConfirmedAt,
            ShippedAt = state.ShippedAt,
            DeliveredAt = state.DeliveredAt,
            CancelledAt = state.CancelledAt,
            TrackingNumber = state.TrackingNumber,
            Carrier = state.Carrier,
            CancellationReason = state.CancellationReason,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderShipped e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummaryEntity> Apply(OrderSummaryEntity state, OrderShipped e, ProjectionContext ctx)
    {
        return new OrderSummaryEntity
        {
            TenantId = state.TenantId,
            DocumentId = state.DocumentId,
            OrderId = state.OrderId,
            CustomerId = state.CustomerId,
            LineItems = state.LineItems,
            Notes = state.Notes,
            Status = OrderStatus.Shipped,
            Total = state.Total,
            CreatedAt = state.CreatedAt,
            ConfirmedAt = state.ConfirmedAt,
            ShippedAt = e.ShippedAt,
            DeliveredAt = state.DeliveredAt,
            CancelledAt = state.CancelledAt,
            TrackingNumber = e.TrackingNumber,
            Carrier = e.Carrier,
            CancellationReason = state.CancellationReason,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderDelivered e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummaryEntity> Apply(OrderSummaryEntity state, OrderDelivered e, ProjectionContext ctx)
    {
        return new OrderSummaryEntity
        {
            TenantId = state.TenantId,
            DocumentId = state.DocumentId,
            OrderId = state.OrderId,
            CustomerId = state.CustomerId,
            LineItems = state.LineItems,
            Notes = state.Notes,
            Status = OrderStatus.Delivered,
            Total = state.Total,
            CreatedAt = state.CreatedAt,
            ConfirmedAt = state.ConfirmedAt,
            ShippedAt = state.ShippedAt,
            DeliveredAt = e.DeliveredAt,
            CancelledAt = state.CancelledAt,
            TrackingNumber = state.TrackingNumber,
            Carrier = state.Carrier,
            CancellationReason = state.CancellationReason,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderCancelled e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummaryEntity> Apply(OrderSummaryEntity state, OrderCancelled e, ProjectionContext ctx)
    {
        return new OrderSummaryEntity
        {
            TenantId = state.TenantId,
            DocumentId = state.DocumentId,
            OrderId = state.OrderId,
            CustomerId = state.CustomerId,
            LineItems = state.LineItems,
            Notes = state.Notes,
            Status = OrderStatus.Cancelled,
            Total = state.Total,
            CreatedAt = state.CreatedAt,
            ConfirmedAt = state.ConfirmedAt,
            ShippedAt = state.ShippedAt,
            DeliveredAt = state.DeliveredAt,
            CancelledAt = e.CancelledAt,
            TrackingNumber = state.TrackingNumber,
            Carrier = state.Carrier,
            CancellationReason = e.Reason,
            UpdatedAt = ctx.Timestamp
        };
    }
}
