using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure.Entities;

namespace Alberto.Orders.Infrastructure.Projections;

/// <summary>
/// EF-based projection that builds OrderSummaryEntity read models from order events.
/// Uses proper relational columns instead of JSONB for rich SQL querying.
/// </summary>
public static class OrderSummaryEfProjection
{
    public static readonly ProjectionDeclaration<OrderSummaryEntity> Declaration =
        DeclareProjection.For<OrderSummaryEntity>(nameof(OrderSummaryEfProjection))
            .Handles<OrderCreated>()
            .Handles<OrderItemAdded>()
            .Handles<OrderItemRemoved>()
            .Handles<OrderConfirmed>()
            .Handles<OrderShipped>()
            .Handles<OrderDelivered>()
            .Handles<OrderCancelled>()
            .DocumentId(e => e.EventType.Id switch
            {
                "order-created" => e.ParseEvent<OrderCreated>()!.OrderId.ToString(),
                "order-item-added" => e.ParseEvent<OrderItemAdded>()!.OrderId.ToString(),
                "order-item-removed" => e.ParseEvent<OrderItemRemoved>()!.OrderId.ToString(),
                "order-confirmed" => e.ParseEvent<OrderConfirmed>()!.OrderId.ToString(),
                "order-shipped" => e.ParseEvent<OrderShipped>()!.OrderId.ToString(),
                "order-delivered" => e.ParseEvent<OrderDelivered>()!.OrderId.ToString(),
                "order-cancelled" => e.ParseEvent<OrderCancelled>()!.OrderId.ToString(),
                _ => null
            })
            .Evolve(Evolve)
            .Build();

    private static ProjectionResult<OrderSummaryEntity> Evolve(OrderSummaryEntity state, IEventEnvelope e, ProjectionContext ctx)
        => e.EventType.Id switch
        {
            "order-created" => Apply(state, e.ParseEvent<OrderCreated>()!, ctx),
            "order-item-added" => Apply(state, e.ParseEvent<OrderItemAdded>()!, ctx),
            "order-item-removed" => Apply(state, e.ParseEvent<OrderItemRemoved>()!, ctx),
            "order-confirmed" => Apply(state, e.ParseEvent<OrderConfirmed>()!, ctx),
            "order-shipped" => Apply(state, e.ParseEvent<OrderShipped>()!, ctx),
            "order-delivered" => Apply(state, e.ParseEvent<OrderDelivered>()!, ctx),
            "order-cancelled" => Apply(state, e.ParseEvent<OrderCancelled>()!, ctx),
            _ => state
        };

    private static OrderSummaryEntity Apply(OrderSummaryEntity state, OrderCreated e, ProjectionContext ctx)
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

    private static OrderSummaryEntity Apply(OrderSummaryEntity state, OrderItemAdded e, ProjectionContext ctx)
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

    private static OrderSummaryEntity Apply(OrderSummaryEntity state, OrderItemRemoved e, ProjectionContext ctx)
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

    private static OrderSummaryEntity Apply(OrderSummaryEntity state, OrderConfirmed e, ProjectionContext ctx)
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

    private static OrderSummaryEntity Apply(OrderSummaryEntity state, OrderShipped e, ProjectionContext ctx)
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

    private static OrderSummaryEntity Apply(OrderSummaryEntity state, OrderDelivered e, ProjectionContext ctx)
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

    private static OrderSummaryEntity Apply(OrderSummaryEntity state, OrderCancelled e, ProjectionContext ctx)
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
