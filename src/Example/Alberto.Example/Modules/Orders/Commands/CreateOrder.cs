using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.CQRS.Validation;
using Alberto.EventSourcing;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.EventHandlers;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record CreateOrderCommand(decimal Amount, string CustomerId) : ICommand;

public sealed class CreateOrderValidator : IValidator<CreateOrderCommand>
{
    public Result Validate(CreateOrderCommand command)
    {
        if (command.Amount <= 0)
            return Result.Fail(Problem.Create("INVALID_AMOUNT", "Order amount must be greater than zero"));

        if (string.IsNullOrWhiteSpace(command.CustomerId))
            return Result.Fail(Problem.Create("INVALID_CUSTOMER", "Customer ID is required"));

        return Result.Success();
    }
}

internal sealed class CreateOrderDecider
{
    public static Decision<Guid> Decide(decimal amount, string customerId)
    {
        var orderId = Guid.CreateVersion7();
        var orderCreated = new OrderCreated(orderId, amount, customerId);

        return Decision<Guid>.Succeed(orderId, orderCreated);
    }
}

public sealed class CreateOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<CreateOrderCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateOrderCommand command, CancellationToken cancellationToken = default)
    {
        var decision = CreateOrderDecider.Decide(command.Amount, command.CustomerId);

        var orderId = decision.Value;
        var query = new StreamQuery([new EventTag(Tags.Order, orderId.ToString())]);

        await eventStore.PersistNew(query, decision.Events, cancellationToken);

        return Result<Guid>.Success(orderId);
    }
}