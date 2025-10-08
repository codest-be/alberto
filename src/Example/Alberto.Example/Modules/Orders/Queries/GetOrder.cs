using Alberto.CQRS.Queries;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.Api.Contracts;

namespace Alberto.Example.Modules.Orders.Queries;

public sealed record GetOrderQuery(Guid OrderId) : IQuery;

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