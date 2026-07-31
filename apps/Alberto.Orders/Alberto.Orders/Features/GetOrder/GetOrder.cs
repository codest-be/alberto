using Alberto;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

/// <summary>
/// What <c>getOrder</c> shows. This is the widest state in the module because the field exposes
/// the whole order — but it is still one slice's state, folded by one slice's evolver, and no
/// decision depends on it.
/// </summary>
public sealed record GetOrderState
{
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; init; }
    public IReadOnlyList<OrderLineItem> LineItems { get; init; } = [];
    public string? Notes { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;
    public string? TrackingNumber { get; init; }
    public string? Carrier { get; init; }
    public string? CancellationReason { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public DateTimeOffset? ShippedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }

    public bool Exists => OrderId != Guid.Empty;
    public decimal Total => LineItems.Sum(x => x.Total);
}

public sealed class GetOrderEvolver : Evolver<GetOrderState>,
    IEvolve<GetOrderState, OrderCreated>,
    IEvolve<GetOrderState, OrderItemAdded>,
    IEvolve<GetOrderState, OrderItemRemoved>,
    IEvolve<GetOrderState, OrderConfirmed>,
    IEvolve<GetOrderState, OrderShipped>,
    IEvolve<GetOrderState, OrderDelivered>,
    IEvolve<GetOrderState, OrderCancelled>
{
    public GetOrderState Apply(GetOrderState s, OrderCreated e) => s with
    {
        OrderId = e.OrderId,
        CustomerId = e.CustomerId,
        LineItems = e.LineItems,
        Notes = e.Notes,
        Status = OrderStatus.Draft
    };

    // Adding a product already on the order replaces its line, as OrderItemAdded means everywhere.
    public GetOrderState Apply(GetOrderState s, OrderItemAdded e) => s with
    {
        LineItems = s.LineItems
            .Where(x => x.ProductId != e.ProductId)
            .Append(new OrderLineItem(e.ProductId, e.ProductName, e.Quantity, e.UnitPrice))
            .ToList()
    };

    public GetOrderState Apply(GetOrderState s, OrderItemRemoved e) => s with
    {
        LineItems = s.LineItems.Where(x => x.ProductId != e.ProductId).ToList()
    };

    public GetOrderState Apply(GetOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed, ConfirmedAt = e.ConfirmedAt };

    public GetOrderState Apply(GetOrderState s, OrderShipped e) => s with
    {
        Status = OrderStatus.Shipped,
        TrackingNumber = e.TrackingNumber,
        Carrier = e.Carrier,
        ShippedAt = e.ShippedAt
    };

    public GetOrderState Apply(GetOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered, DeliveredAt = e.DeliveredAt };

    public GetOrderState Apply(GetOrderState s, OrderCancelled e) => s with
    {
        Status = OrderStatus.Cancelled,
        CancellationReason = e.Reason,
        CancelledAt = e.CancelledAt
    };
}

public static class GetOrderQuery
{
    private static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

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
        var events = await backend.StreamAsync(Boundary(orderId), cancellationToken: ct);
        var state = new GetOrderEvolver().Reconstitute(events);

        return state.Exists ? ToGraphQL(state) : null;
    }

    private static Order ToGraphQL(GetOrderState state) => new(
        state.OrderId,
        state.CustomerId,
        state.LineItems
            .Select(x => new OrderItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice, x.Total))
            .ToList(),
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
}
