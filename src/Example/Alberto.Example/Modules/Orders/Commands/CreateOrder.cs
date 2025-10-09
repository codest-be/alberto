using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.EventHandlers;
using FluentValidation;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record CreateOrderCommand(decimal Amount, string CustomerId) : ICommand;

public sealed class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .WithErrorCode("INVALID_AMOUNT")
            .WithMessage("Order amount must be greater than zero");

        RuleFor(x => x.CustomerId)
            .NotEmpty()
            .WithErrorCode("INVALID_CUSTOMER")
            .WithMessage("Customer ID is required");
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

internal sealed class CreateOrderDecider
{
    public static Decision<Guid> Decide(decimal amount, string customerId)
    {
        var orderId = Guid.CreateVersion7();
        var orderCreated = new OrderCreated(orderId, amount, customerId);

        return Decision<Guid>.Succeed(orderId, orderCreated);
    }
}