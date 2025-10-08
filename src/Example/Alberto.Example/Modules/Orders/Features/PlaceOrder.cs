using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.EventHandlers;

namespace Alberto.Example.Modules.Orders.Features;

public static class PlaceOrderEndpoint
{
    public static IEndpointRouteBuilder MapPlaceOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{orderId:guid}/place", async (
                Guid orderId,
                ICommandHandler<PlaceOrderCommand, bool> handler,
                CancellationToken ct) =>
            {
                var command = new PlaceOrderCommand(orderId);
                var result = await handler.Handle(command, ct);

                return result.ToHttpResult();
            })
            .WithName("PlaceOrder")
            .WithOpenApi();

        return endpoints;
    }
}

public sealed record PlaceOrderCommand(Guid OrderId) : ICommand;

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
            OrderCreated e => state with
            {
                Exists = true, Status = OrderStatus.Created, Amount = e.Amount, CustomerId = e.CustomerId
            },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            OrderCancelled => state with { Status = OrderStatus.Cancelled },
            _ => state
        };
    }
}

internal static class PlaceOrderDecider
{
    public static Decision Decide(PlaceOrderState state, PlaceOrderCommand command)
    {
        if (!state.Exists)
            return Decision.Fail(Problem.Create("ORDER_NOT_FOUND", $"Order {command.OrderId} does not exist"));

        if (state.Status != OrderStatus.Created)
            return Decision.Fail(Problem.Create(
                "INVALID_ORDER_STATUS",
                $"Order must be in Created status to be placed. Current status: {state.Status}"));

        var orderPlaced = new OrderPlaced(command.OrderId, state.Amount, state.CustomerId);
        return Decision.Succeed(orderPlaced);
    }
}

public sealed class PlaceOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<PlaceOrderCommand, bool>
{
    public async Task<Result<bool>> Handle(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        // Query only events that affect placement decisions
        var query = new StreamQuery([new EventTag(Tags.Order, command.OrderId.ToString())])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderCancelled>();

        var projector = new PlaceOrderProjector();
        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = projector.Evolve(events);

        var decision = PlaceOrderDecider.Decide(state, command);

        if (decision.IsError)
            return Result<bool>.Fail(decision.Problems.First());

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);

        return Result<bool>.Success(true);
    }
}