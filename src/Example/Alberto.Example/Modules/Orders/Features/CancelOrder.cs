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

// Minimal state for cancel order decision - only what's needed for cancellation
internal sealed record CancelOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
}

// Minimal projector - only processes events affecting cancellation decisions
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

// Decider function for cancel order business logic
internal static class CancelOrderDecider
{
    public static Decision Decide(CancelOrderState state, CancelOrderCommand command)
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
        // Query only events that affect cancellation decisions
        var query = new StreamQuery([new EventTag(Tags.Order, command.OrderId.ToString())])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderShipped>()
            .WithEventType<OrderCancelled>();

        var projector = new CancelOrderProjector();
        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = projector.Evolve(events);

        var decision = CancelOrderDecider.Decide(state, command);

        if (decision.IsError)
            return Result<bool>.Fail(decision.Problems.First());

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);

        return Result<bool>.Success(true);
    }
}