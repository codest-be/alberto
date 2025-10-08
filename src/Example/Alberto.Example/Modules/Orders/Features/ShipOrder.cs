using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.CQRS.Validation;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.EventHandlers;

namespace Alberto.Example.Modules.Orders.Features;

public static class ShipOrderEndpoint
{
    public static IEndpointRouteBuilder MapShipOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{orderId:guid}/ship", async (
                Guid orderId,
                ShipOrderRequest request,
                ICommandHandler<ShipOrderCommand, bool> handler,
                CancellationToken ct) =>
            {
                var command = new ShipOrderCommand(orderId, request.TrackingNumber);
                var result = await handler.Handle(command, ct);

                return result.ToHttpResult();
            })
            .WithName("ShipOrder")
            .WithOpenApi();

        return endpoints;
    }
}

public sealed record ShipOrderRequest(string TrackingNumber);

public sealed record ShipOrderCommand(Guid OrderId, string TrackingNumber) : ICommand;

public sealed class ShipOrderValidator : IValidator<ShipOrderCommand>
{
    public Result Validate(ShipOrderCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.TrackingNumber))
            return Result.Fail(Problem.Create("INVALID_TRACKING_NUMBER", "Tracking number is required"));

        return Result.Success();
    }
}

internal sealed record ShipOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
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

    public Decision Decide(ShipOrderState state, ShipOrderCommand command)
    {
        if (!state.Exists)
            return Decision.Fail(Problem.Create("ORDER_NOT_FOUND", $"Order {command.OrderId} does not exist"));

        if (state.Status != OrderStatus.Placed)
            return Decision.Fail(Problem.Create(
                "INVALID_ORDER_STATUS",
                $"Order must be in Placed status to be shipped. Current status: {state.Status}"));

        var orderShipped = new OrderShipped(command.OrderId, command.TrackingNumber);
        return Decision.Succeed(orderShipped);
    }
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
        var decision = decider.Decide(state, command);

        if (decision.IsError)
            return Result<bool>.Fail(decision.Problems.First());

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);

        return Result<bool>.Success(true);
    }
}