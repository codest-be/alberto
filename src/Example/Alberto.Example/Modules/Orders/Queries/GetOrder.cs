using Alberto.CQRS.Queries;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projections;
using Alberto.Example.Modules.Orders.Api.Contracts;
using Alberto.Example.Modules.Orders.Projections;

namespace Alberto.Example.Modules.Orders.Queries;

public sealed record GetOrderQuery(Guid OrderId) : IQuery;

public sealed class GetOrderHandler(IProjectionRepository<Guid, OrderState> repository)
    : IQueryHandler<GetOrderQuery, OrderDto>
{
    public async Task<Result<OrderDto>> Handle(GetOrderQuery query, CancellationToken cancellationToken = default)
    {
        var order = await repository.Get(query.OrderId, cancellationToken);

        if (order == null)
            return Result<OrderDto>.Fail(Problem.Create("ORDER_NOT_FOUND", $"Order {query.OrderId} does not exist"));

        var dto = new OrderDto(
            order.OrderId,
            order.Amount,
            order.CustomerId,
            order.Status.ToString(),
            order.TrackingNumber,
            order.CancellationReason);

        return Result<OrderDto>.Success(dto);
    }
}