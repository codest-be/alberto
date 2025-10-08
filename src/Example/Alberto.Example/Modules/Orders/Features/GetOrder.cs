using Alberto.CQRS.Queries;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventStore;
using Alberto.EventStore.Events;

namespace Alberto.Example.Modules.Orders.Features;

public static class GetOrderEndpoint
{
    public static IEndpointRouteBuilder MapGetOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/orders/{orderId:guid}", async (
                Guid orderId,
                IQueryHandler<GetOrderQuery, OrderDto> handler,
                CancellationToken ct) =>
            {
                var query = new GetOrderQuery(orderId);
                var result = await handler.Handle(query, ct);

                return result.ToHttpResult();
            })
            .WithName("GetOrder")
            .WithOpenApi();

        return endpoints;
    }
}

public sealed record GetOrderQuery(Guid OrderId) : IQuery;

public sealed record OrderDto(
    Guid OrderId,
    decimal Amount,
    string CustomerId,
    string Status,
    string? TrackingNumber,
    string? CancellationReason);

public sealed class GetOrderHandler(IEventSourcedRepository<OrderState> repository)
    : IQueryHandler<GetOrderQuery, OrderDto>
{
    public async Task<Result<OrderDto>> Handle(GetOrderQuery query, CancellationToken cancellationToken = default)
    {
        var streamQuery = new StreamQuery([new EventTag(Tags.Order, query.OrderId.ToString())]);
        var aggregate = await repository.Load(streamQuery, cancellationToken);

        if (aggregate.IsNew)
            return Result<OrderDto>.Fail(Problem.Create("ORDER_NOT_FOUND", $"Order {query.OrderId} does not exist"));

        var dto = new OrderDto(
            aggregate.State.OrderId,
            aggregate.State.Amount,
            aggregate.State.CustomerId,
            aggregate.State.Status.ToString(),
            aggregate.State.TrackingNumber,
            aggregate.State.CancellationReason);

        return Result<OrderDto>.Success(dto);
    }
}