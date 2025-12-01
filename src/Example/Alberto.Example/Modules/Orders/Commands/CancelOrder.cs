using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record CancelOrderCommand(Guid OrderId, string Reason) : ICommand;

public sealed class CancelOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<CancelOrderCommand>
{
    public async Task<Result> Handle(CancelOrderCommand command, CancellationToken cancellationToken = default)
    {
        var query = new StreamQuery([new EventTag(Tags.Order, command.OrderId.ToString())])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderShipped>()
            .WithEventType<OrderCancelled>();

        var decision = await eventStore.Decide(
            new CancelOrderProjector(),
            query,
            state => CancelOrderDecision.Decide(state, command.OrderId, command.Reason),
            cancellationToken);

        return decision.IsError
            ? Result.Fail(decision.Problems.First())
            : Result.Success();
    }
}

internal sealed record CancelOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
}

internal sealed class CancelOrderProjector : IProjector<CancelOrderState>
{
    public CancelOrderState Apply(CancelOrderState state, object @event)
    {
        return @event switch
        {
            OrderCreated => state with { Exists = true, Status = OrderStatus.Created },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            OrderShipped => state with { Status = OrderStatus.Shipped },
            OrderCancelled => state with { Status = OrderStatus.Cancelled },
            _ => state
        };
    }
}

internal static class CancelOrderDecision
{
    public static Decision Decide(CancelOrderState state, Guid orderId, string reason)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.OrderNotFound(orderId));

        if (state.Status == OrderStatus.Cancelled)
            return Decision.Fail(OrderProblems.OrderAlreadyCancelled());

        if (state.Status == OrderStatus.Shipped)
            return Decision.Fail(OrderProblems.CannotCancelShippedOrder());

        var orderCancelled = new OrderCancelled(orderId, reason);
        return Decision.Succeed(orderCancelled);
    }
}