using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.EventHandlers;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record PlaceOrderCommand(Guid OrderId) : ICommand;

public sealed class PlaceOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<PlaceOrderCommand, bool>
{
    public async Task<Result<bool>> Handle(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        var decider = new PlaceOrderDecider();
        var query = PlaceOrderDecider.GetQuery(command.OrderId);

        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = decider.Evolve(events);
        var decision = decider.Decide(state, command.OrderId);

        if (decision.IsError)
            return Result<bool>.Fail(decision.Problems.First());

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);

        return Result<bool>.Success(true);
    }
}

internal sealed record PlaceOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
    public decimal Amount { get; init; }
    public string CustomerId { get; init; } = string.Empty;
}

internal sealed class PlaceOrderDecider : IProjector<PlaceOrderState>
{
    public PlaceOrderState Apply(PlaceOrderState state, object @event)
    {
        return @event switch
        {
            OrderCreated e => state with
            {
                Exists = true, Status = OrderStatus.Created, Amount = e.Amount, CustomerId = e.CustomerId
            },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            OrderCancelled => state with { Status = OrderStatus.Cancelled },
            _ => state
        };
    }

    public static StreamQuery GetQuery(Guid orderId)
    {
        return new StreamQuery([new EventTag(Tags.Order, orderId.ToString())])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderCancelled>();
    }

    public Decision Decide(PlaceOrderState state, Guid orderId)
    {
        if (!state.Exists)
            return Decision.Fail(Problem.Create("ORDER_NOT_FOUND", $"Order {orderId} does not exist"));

        if (state.Status != OrderStatus.Created)
            return Decision.Fail(Problem.Create(
                "INVALID_ORDER_STATUS",
                $"Order must be in Created status to be placed. Current status: {state.Status}"));

        var orderPlaced = new OrderPlaced(orderId, state.Amount, state.CustomerId);
        return Decision.Succeed(orderPlaced);
    }
}