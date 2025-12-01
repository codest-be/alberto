using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.Events;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record CreateOrderCommand(decimal Amount, string CustomerId) : ICommand<Guid>;

public sealed class CreateOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<CreateOrderCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateOrderCommand command, CancellationToken cancellationToken = default)
    {
        var orderId = Guid.CreateVersion7();
        var query = new StreamQuery([new EventTag(Tags.Order, orderId.ToString())]);
        var decision = await eventStore.DecideNew(
            query,
            () => CreateOrderDecision.Decide(command.Amount, command.CustomerId, orderId),
            cancellationToken);

        return decision.IsError
            ? Result<Guid>.Fail(decision.Problems)
            : Result<Guid>.Success(decision.Value);
    }
}

internal static class CreateOrderDecision
{
    public static Decision<Guid> Decide(decimal amount, string customerId, Guid orderId)
    {
        var orderCreated = new OrderCreated(orderId, amount, customerId);
        return Decision<Guid>.Succeed(orderId, orderCreated);
    }
}