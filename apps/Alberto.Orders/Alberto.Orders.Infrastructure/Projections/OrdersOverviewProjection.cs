using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure.ReadModels;

namespace Alberto.Orders.Infrastructure.Projections;

public static class OrdersOverviewProjection
{
    public const string DocumentId = "overview";

    public static readonly ProjectionDeclaration<OrdersOverview> Declaration =
        DeclareProjection.For<OrdersOverview>(nameof(OrdersOverviewProjection))
            .On<OrderCreated>(
                id: _ => DocumentId,
                apply: (state, e, ctx) => ApplyOrderCreated(state, e, ctx))
            .On<OrderConfirmed>(
                id: _ => DocumentId,
                apply: (state, _, ctx) => state with { DraftOrders = Math.Max(0, state.DraftOrders - 1), ConfirmedOrders = state.ConfirmedOrders + 1, UpdatedAt = ctx.Timestamp })
            .On<OrderShipped>(
                id: _ => DocumentId,
                apply: (state, _, ctx) => state with { ConfirmedOrders = Math.Max(0, state.ConfirmedOrders - 1), ShippedOrders = state.ShippedOrders + 1, UpdatedAt = ctx.Timestamp })
            .On<OrderDelivered>(
                id: _ => DocumentId,
                apply: (state, _, ctx) => state with { ShippedOrders = Math.Max(0, state.ShippedOrders - 1), DeliveredOrders = state.DeliveredOrders + 1, UpdatedAt = ctx.Timestamp })
            .On<OrderCancelled>(
                id: _ => DocumentId,
                apply: (state, _, ctx) => state with { DraftOrders = Math.Max(0, state.DraftOrders - 1), CancelledOrders = state.CancelledOrders + 1, UpdatedAt = ctx.Timestamp })
            .Build();

    private static OrdersOverview ApplyOrderCreated(OrdersOverview state, OrderCreated e, ProjectionContext ctx)
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
}
