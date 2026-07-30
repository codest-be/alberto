using Alberto.Dcb;
using Microsoft.Extensions.DependencyInjection;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>
/// GraphQL queries for orders.
/// </summary>
/// <remarks>
/// Staging: what is left here moves into <c>Features/GetOrder</c> and
/// <c>Features/OrdersOverview</c>, after which this file goes.
/// </remarks>
public static class OrderQueries
{
    private static readonly OrderEvolver _evolver = new();

    /// <summary>
    /// Gets an order by ID from the event store (real-time, consistent).
    /// </summary>
    [Query]
    [GraphQLDescription("Gets an order by ID, rebuilt from events for consistency.")]
    public static async Task<Order?> GetOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);
        var state = await LoadOrderState(backend, orderId, ct);

        if (!state.Exists)
            return null;

        return ToGraphQL(state);
    }

    /// <summary>
    /// Gets the orders overview statistics from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets aggregated order statistics from the async projection.")]
    public static async Task<OrdersOverview?> GetOrdersOverview(
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        // OrdersOverviewProjection is a cross-tenant aggregate: the control loop accumulates
        // events from every tenant into a single document, stored under TenantScope.CrossTenant
        // because a tenant-enabled module's projection rows are keyed by tenant and this one
        // belongs to no single tenant. The factory resolved here is the writer's own, so the
        // only thing this resolver decides is which tenant to read — and passing the request's
        // tenant here would be wrong, not merely empty.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<OrdersOverview>>>(
            $"{OrdersModule.ModuleKey}:{nameof(OrdersOverviewProjection)}");
        var stateStore = factory(TenantScope.CrossTenant);

        var states = await stateStore.LoadManyAsync(
            [OrdersOverviewProjection.DocumentId],
            ct: ct);

        return states.GetValueOrDefault(OrdersOverviewProjection.DocumentId);
    }

    #region Helper Methods

    private static async Task<OrderState> LoadOrderState(
        IEventStoreBackend backend,
        Guid orderId,
        CancellationToken ct)
    {
        var events = await backend.StreamAsync(OrderDecider.BoundaryFor(orderId), cancellationToken: ct);
        return _evolver.Reconstitute(events);
    }

    private static Order ToGraphQL(OrderState state) => new(
        state.OrderId,
        state.CustomerId,
        state.LineItems.Select(x => new OrderItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice, x.Total)).ToList(),
        state.Notes,
        state.Status,
        state.Total,
        state.TrackingNumber,
        state.Carrier,
        state.CancellationReason,
        DateTimeOffset.MinValue, // Would need to track this in state
        state.ConfirmedAt,
        state.ShippedAt,
        state.DeliveredAt,
        state.CancelledAt,
        null);

    #endregion
}
