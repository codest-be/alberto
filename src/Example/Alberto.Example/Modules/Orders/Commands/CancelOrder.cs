using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.EventHandlers;
using FluentValidation;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record CancelOrderCommand(Guid OrderId, string Reason) : ICommand;

public sealed class CancelOrderValidator : AbstractValidator<CancelOrderCommand>
{
    public CancelOrderValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty()
            .WithErrorCode("INVALID_REASON")
            .WithMessage("Cancellation reason is required");
    }
}

internal sealed record CancelOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
}

public sealed class CancelOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<CancelOrderCommand, bool>
{
    public async Task<Result<bool>> Handle(CancelOrderCommand command, CancellationToken cancellationToken = default)
    {
        var query = CancelOrderDecider.GetQuery(command.OrderId);

        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = new CancelOrderDecider().Evolve(events);
        var decision = CancelOrderDecider.Decide(state, command.OrderId, command.Reason);

        if (decision.IsError)
            return Result<bool>.Fail(decision.Problems.First());

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);

        return Result<bool>.Success(true);
    }
}

internal sealed class CancelOrderDecider : IProjector<CancelOrderState>
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

    public static StreamQuery GetQuery(Guid orderId) =>
        new StreamQuery([new EventTag(Tags.Order, orderId.ToString())])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderShipped>()
            .WithEventType<OrderCancelled>();

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