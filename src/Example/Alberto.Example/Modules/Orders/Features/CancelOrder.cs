using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.CQRS.Validation;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.EventHandlers;

namespace Alberto.Example.Modules.Orders.Features;

public static class CancelOrderEndpoint
{
    public static IEndpointRouteBuilder MapCancelOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{orderId:guid}/cancel", async (
                Guid orderId,
                CancelOrderRequest request,
                ICommandHandler<CancelOrderCommand, bool> handler,
                CancellationToken ct) =>
            {
                var command = new CancelOrderCommand(orderId, request.Reason);
                var result = await handler.Handle(command, ct);

                return result.ToHttpResult();
            })
            .WithName("CancelOrder")
            .WithOpenApi();

        return endpoints;
    }
}

public sealed record CancelOrderRequest(string Reason);

public sealed record CancelOrderCommand(Guid OrderId, string Reason) : ICommand;

public sealed class CancelOrderValidator : IValidator<CancelOrderCommand>
{
    public Result Validate(CancelOrderCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
            return Result.Fail(Problem.Create("INVALID_REASON", "Cancellation reason is required"));

        return Result.Success();
    }
}

internal sealed record CancelOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
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

    public Decision Decide(CancelOrderState state, CancelOrderCommand command)
    {
        if (!state.Exists)
            return Decision.Fail(Problem.Create("ORDER_NOT_FOUND", $"Order {command.OrderId} does not exist"));

        if (state.Status == OrderStatus.Cancelled)
            return Decision.Fail(Problem.Create(
                "ORDER_ALREADY_CANCELLED",
                "Order is already cancelled"));

        if (state.Status == OrderStatus.Shipped)
            return Decision.Fail(Problem.Create(
                "CANNOT_CANCEL_SHIPPED_ORDER",
                "Cannot cancel an order that has already been shipped"));

        var orderCancelled = new OrderCancelled(command.OrderId, command.Reason);
        return Decision.Succeed(orderCancelled);
    }
}

public sealed class CancelOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<CancelOrderCommand, bool>
{
    public async Task<Result<bool>> Handle(CancelOrderCommand command, CancellationToken cancellationToken = default)
    {
        var decider = new CancelOrderDecider();
        var query = CancelOrderDecider.GetQuery(command.OrderId);

        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = decider.Evolve(events);
        var decision = decider.Decide(state, command);

        if (decision.IsError)
            return Result<bool>.Fail(decision.Problems.First());

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);

        return Result<bool>.Success(true);
    }
}