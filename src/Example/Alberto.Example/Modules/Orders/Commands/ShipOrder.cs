using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;
using FluentValidation;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record ShipOrderCommand(Guid OrderId, string TrackingNumber) : ICommand;

public sealed class ShipOrderValidator : AbstractValidator<ShipOrderCommand>
{
    public ShipOrderValidator()
    {
        RuleFor(x => x.TrackingNumber)
            .NotEmpty()
            .WithErrorCode("INVALID_TRACKING_NUMBER")
            .WithMessage("Tracking number is required");
    }
}

internal sealed record ShipOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
}

public sealed class ShipOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<ShipOrderCommand, bool>
{
    public async Task<Result<bool>> Handle(ShipOrderCommand command, CancellationToken cancellationToken = default)
    {
        var decider = new ShipOrderDecider();
        var query = ShipOrderDecider.GetQuery(command.OrderId);

        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = decider.Evolve(events);
        var decision = decider.Decide(state, command.OrderId, command.TrackingNumber);

        if (decision.IsError)
            return Result<bool>.Fail(decision.Problems.First());

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);

        return Result<bool>.Success(true);
    }
}

internal sealed class ShipOrderDecider : IProjector<ShipOrderState>
{
    public ShipOrderState Apply(ShipOrderState state, object @event)
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

    public static StreamQuery GetQuery(Guid orderId)
    {
        return new StreamQuery([new EventTag(Tags.Order, orderId.ToString())])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderShipped>()
            .WithEventType<OrderCancelled>();
    }

    public Decision Decide(ShipOrderState state, Guid orderId, string trackingNumber)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.OrderNotFound(orderId));

        if (state.Status != OrderStatus.Placed)
            return Decision.Fail(OrderProblems.InvalidStatusForShipping(state.Status));

        var orderShipped = new OrderShipped(orderId, trackingNumber);
        return Decision.Succeed(orderShipped);
    }
}