using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Orders.Core;
using Alberto.Orders.Infrastructure.ReadModels;

namespace Alberto.Orders.Infrastructure.Projections;

/// <summary>
/// Projection that aggregates order statistics into a single overview document.
/// Uses a fixed document ID per tenant to maintain a singleton aggregate.
/// </summary>
public sealed class OrdersOverviewProjection : Projection<OrdersOverview>,
    IProject<OrdersOverview, OrderCreated>,
    IProject<OrdersOverview, OrderConfirmed>,
    IProject<OrdersOverview, OrderShipped>,
    IProject<OrdersOverview, OrderDelivered>,
    IProject<OrdersOverview, OrderCancelled>
{
    /// <summary>
    /// Fixed document ID for the overview aggregate.
    /// </summary>
    public const string DocumentId = "overview";

    public string GetDocumentId(OrderCreated e) => DocumentId;
    public ProjectionResult<OrdersOverview> Apply(OrdersOverview state, OrderCreated e, ProjectionContext ctx)
    {
        var orderTotal = e.LineItems.Sum(x => x.Total);
        var newTotalOrders = state.TotalOrders + 1;
        var newTotalRevenue = state.TotalRevenue + orderTotal;

        return state with
        {
            TotalOrders = newTotalOrders,
            DraftOrders = state.DraftOrders + 1,
            TotalRevenue = newTotalRevenue,
            AverageOrderValue = newTotalOrders > 0 ? newTotalRevenue / newTotalOrders : 0,
            LastOrderAt = ctx.Timestamp,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderConfirmed e) => DocumentId;
    public ProjectionResult<OrdersOverview> Apply(OrdersOverview state, OrderConfirmed e, ProjectionContext ctx)
    {
        return state with
        {
            DraftOrders = Math.Max(0, state.DraftOrders - 1),
            ConfirmedOrders = state.ConfirmedOrders + 1,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderShipped e) => DocumentId;
    public ProjectionResult<OrdersOverview> Apply(OrdersOverview state, OrderShipped e, ProjectionContext ctx)
    {
        return state with
        {
            ConfirmedOrders = Math.Max(0, state.ConfirmedOrders - 1),
            ShippedOrders = state.ShippedOrders + 1,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderDelivered e) => DocumentId;
    public ProjectionResult<OrdersOverview> Apply(OrdersOverview state, OrderDelivered e, ProjectionContext ctx)
    {
        return state with
        {
            ShippedOrders = Math.Max(0, state.ShippedOrders - 1),
            DeliveredOrders = state.DeliveredOrders + 1,
            UpdatedAt = ctx.Timestamp
        };
    }

    public string GetDocumentId(OrderCancelled e) => DocumentId;
    public ProjectionResult<OrdersOverview> Apply(OrdersOverview state, OrderCancelled e, ProjectionContext ctx)
    {
        // Note: In a real system, we'd need to know the previous status
        // to correctly decrement the right counter. For simplicity,
        // we assume most cancellations come from draft orders.
        return state with
        {
            DraftOrders = Math.Max(0, state.DraftOrders - 1),
            CancelledOrders = state.CancelledOrders + 1,
            UpdatedAt = ctx.Timestamp
        };
    }
}
