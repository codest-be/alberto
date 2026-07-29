using Alberto.Orders.Contracts;
using Alberto.Dcb;
using OrderActions = Alberto.Orders.Features.OrderDecider;

namespace Alberto.Orders.Features;

/// <summary>
/// Reconstitutes OrderState from events. Used on the command side to load current state.
/// </summary>
public sealed class OrderEvolver : Evolver<OrderState>,
    IEvolve<OrderState, OrderCreated>,
    IEvolve<OrderState, OrderItemAdded>,
    IEvolve<OrderState, OrderItemRemoved>,
    IEvolve<OrderState, OrderConfirmed>,
    IEvolve<OrderState, OrderShipped>,
    IEvolve<OrderState, OrderDelivered>,
    IEvolve<OrderState, OrderCancelled>
{
    private readonly OrderActions _decider = new();

    public OrderState Apply(OrderState state, OrderCreated e) => _decider.Apply(state, e);
    public OrderState Apply(OrderState state, OrderItemAdded e) => _decider.Apply(state, e);
    public OrderState Apply(OrderState state, OrderItemRemoved e) => _decider.Apply(state, e);
    public OrderState Apply(OrderState state, OrderConfirmed e) => _decider.Apply(state, e);
    public OrderState Apply(OrderState state, OrderShipped e) => _decider.Apply(state, e);
    public OrderState Apply(OrderState state, OrderDelivered e) => _decider.Apply(state, e);
    public OrderState Apply(OrderState state, OrderCancelled e) => _decider.Apply(state, e);
}
