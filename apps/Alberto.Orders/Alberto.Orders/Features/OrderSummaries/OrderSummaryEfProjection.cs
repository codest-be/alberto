using Alberto.Subscriptions;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>
/// EF-based projection that builds <see cref="OrderSummaryEntity"/> read models from order events.
/// Uses proper relational columns instead of JSONB for rich SQL querying.
/// </summary>
/// <remarks>
/// <para>
/// The handlers mutate the supplied state and return it. That is safe — and deliberate — because
/// the state store hands out detached (<c>AsNoTracking</c>) instances, and the batch dispatcher
/// threads the same instance through every event for a document before persisting it once.
/// </para>
/// <para>
/// Mutating beats rebuilding the entity field-by-field: infrastructure columns
/// (<see cref="IProjectionEntity.Version"/>, <see cref="IProjectionEntity.RebuildVersion"/>)
/// and any future column survive without a copy line each, so adding a property to the entity
/// cannot silently drop it here.
/// </para>
/// </remarks>
public static class OrderSummaryEfProjection
{
    public static readonly ProjectionDeclaration<OrderSummaryEntity> Declaration =
        DeclareProjection.For<OrderSummaryEntity>(nameof(OrderSummaryEfProjection))
            .On<OrderCreated>(id: e => e.OrderId.ToString(), apply: Apply)
            .On<OrderItemAdded>(id: e => e.OrderId.ToString(), apply: Apply)
            .On<OrderItemRemoved>(id: e => e.OrderId.ToString(), apply: Apply)
            .On<OrderConfirmed>(id: e => e.OrderId.ToString(), apply: Apply)
            .On<OrderShipped>(id: e => e.OrderId.ToString(), apply: Apply)
            .On<OrderDelivered>(id: e => e.OrderId.ToString(), apply: Apply)
            .On<OrderCancelled>(id: e => e.OrderId.ToString(), apply: Apply)
            .Build();

    private static ProjectionResult<OrderSummaryEntity> Apply(
        OrderSummaryEntity state, OrderCreated e, ProjectionContext ctx)
    {
        state.TenantId = ctx.TenantId ?? string.Empty;
        state.DocumentId = e.OrderId.ToString();
        state.OrderId = e.OrderId;
        state.CustomerId = e.CustomerId;
        state.LineItems = [.. e.LineItems.Select(li => new OrderLineItemData
        {
            ProductId = li.ProductId,
            ProductName = li.ProductName,
            Quantity = li.Quantity,
            UnitPrice = li.UnitPrice,
            Total = li.Total
        })];
        state.Notes = e.Notes;
        state.Status = OrderStatus.Draft;
        state.Total = state.LineItems.Sum(x => x.Total);
        state.CreatedAt = ctx.Timestamp;
        return Touch(state, ctx);
    }

    private static ProjectionResult<OrderSummaryEntity> Apply(
        OrderSummaryEntity state, OrderItemAdded e, ProjectionContext ctx)
    {
        // Adding a product that is already on the order replaces the existing line.
        state.LineItems.RemoveAll(x => x.ProductId == e.ProductId);
        state.LineItems.Add(new OrderLineItemData
        {
            ProductId = e.ProductId,
            ProductName = e.ProductName,
            Quantity = e.Quantity,
            UnitPrice = e.UnitPrice,
            Total = e.Quantity * e.UnitPrice
        });
        state.Total = state.LineItems.Sum(x => x.Total);
        return Touch(state, ctx);
    }

    private static ProjectionResult<OrderSummaryEntity> Apply(
        OrderSummaryEntity state, OrderItemRemoved e, ProjectionContext ctx)
    {
        state.LineItems.RemoveAll(x => x.ProductId == e.ProductId);
        state.Total = state.LineItems.Sum(x => x.Total);
        return Touch(state, ctx);
    }

    private static ProjectionResult<OrderSummaryEntity> Apply(
        OrderSummaryEntity state, OrderConfirmed e, ProjectionContext ctx)
    {
        state.Status = OrderStatus.Confirmed;
        state.ConfirmedAt = e.ConfirmedAt;
        return Touch(state, ctx);
    }

    private static ProjectionResult<OrderSummaryEntity> Apply(
        OrderSummaryEntity state, OrderShipped e, ProjectionContext ctx)
    {
        state.Status = OrderStatus.Shipped;
        state.ShippedAt = e.ShippedAt;
        state.TrackingNumber = e.TrackingNumber;
        state.Carrier = e.Carrier;
        return Touch(state, ctx);
    }

    private static ProjectionResult<OrderSummaryEntity> Apply(
        OrderSummaryEntity state, OrderDelivered e, ProjectionContext ctx)
    {
        state.Status = OrderStatus.Delivered;
        state.DeliveredAt = e.DeliveredAt;
        return Touch(state, ctx);
    }

    private static ProjectionResult<OrderSummaryEntity> Apply(
        OrderSummaryEntity state, OrderCancelled e, ProjectionContext ctx)
    {
        state.Status = OrderStatus.Cancelled;
        state.CancelledAt = e.CancelledAt;
        state.CancellationReason = e.Reason;
        return Touch(state, ctx);
    }

    /// <summary>
    /// Stamps the event timestamp and hands the state back as a <c>Set</c> result.
    /// </summary>
    private static ProjectionResult<OrderSummaryEntity> Touch(OrderSummaryEntity state, ProjectionContext ctx)
    {
        state.UpdatedAt = ctx.Timestamp;
        return state;
    }
}
