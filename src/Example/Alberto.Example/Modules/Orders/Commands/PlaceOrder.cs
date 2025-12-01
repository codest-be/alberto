using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record PlaceOrderCommand(Guid OrderId) : ICommand;

public sealed class PlaceOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<PlaceOrderCommand>
{
    public async Task<Result> Handle(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        var query = new StreamQuery([new EventTag(Tags.Order, command.OrderId.ToString())])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderCancelled>();

        var decision = await eventStore.Decide(
            new PlaceOrderProjector(),
            query,
            state => PlaceOrderDecision.Decide(state, command.OrderId),
            cancellationToken);

        return decision.IsError
            ? Result.Fail(decision.Problems)
            : Result.Success();
    }
}

internal sealed record PlaceOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
    public decimal Amount { get; init; }
    public string CustomerId { get; init; } = string.Empty;
}

internal sealed class PlaceOrderProjector : IProjector<PlaceOrderState>
{
    public PlaceOrderState Apply(PlaceOrderState state, object @event)
    {
        return @event switch
        {
            OrderCreated e => state with { Exists = true, Status = OrderStatus.Created, Amount = e.Amount, CustomerId = e.CustomerId },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            OrderCancelled => state with { Status = OrderStatus.Cancelled },
            _ => state
        };
    }
}

internal static class PlaceOrderDecision
{
    public static Decision Decide(PlaceOrderState state, Guid orderId)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.OrderNotFound(orderId));

        if (state.Status != OrderStatus.Created)
            return Decision.Fail(OrderProblems.InvalidStatusForPlacing(state.Status));

        var orderPlaced = new OrderPlaced(orderId, state.Amount, state.CustomerId);
        return Decision.Succeed(orderPlaced);
    }
}