using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
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

public sealed class PlaceOrderHandler(IEventSourcedRepository<OrderState> repository)
    : ICommandHandler<PlaceOrderCommand, bool>
{
    private readonly IEventSourcedRepository<OrderState> _repository = repository;

    public async Task<Result<bool>> Handle(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        var query = new StreamQuery([new EventTag(Tags.Order, command.OrderId.ToString())]);
        var aggregate = await _repository.Load(query, cancellationToken);

        if (aggregate.IsNew)
            return Result<bool>.Fail(Problem.Create("ORDER_NOT_FOUND", $"Order {command.OrderId} does not exist"));

        if (aggregate.State.Status != OrderStatus.Created)
            return Result<bool>.Fail(Problem.Create(
                "INVALID_ORDER_STATUS",
                $"Order must be in Created status to be placed. Current status: {aggregate.State.Status}"));

        var orderPlaced = new OrderPlaced(
            command.OrderId,
            aggregate.State.Amount,
            aggregate.State.CustomerId);

        await _repository.Save(query, aggregate, [orderPlaced], cancellationToken);

        return Result<bool>.Success(true);
    }
}