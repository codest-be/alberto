using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Payments.Events;
using FluentValidation;

namespace Alberto.Example.Modules.Payments.Commands;

public sealed record CreatePaymentCommand(Guid OrderId, decimal Amount) : ICommand;

public sealed class CreatePaymentValidator : AbstractValidator<CreatePaymentCommand>
{
    public CreatePaymentValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .WithErrorCode("INVALID_AMOUNT")
            .WithMessage("Payment amount must be greater than zero");

        RuleFor(x => x.OrderId)
            .NotEmpty()
            .WithErrorCode("INVALID_ORDER")
            .WithMessage("Order ID is required");
    }
}

public sealed class CreatePaymentHandler(PaymentEventStore eventStore)
    : ICommandHandler<CreatePaymentCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreatePaymentCommand command, CancellationToken cancellationToken = default)
    {
        var decision = new CreatePaymentDecider().Decide(command.OrderId, command.Amount);

        var paymentId = decision.Value;
        var query = new StreamQuery([new EventTag(Tags.Payment, paymentId.ToString())]);

        await eventStore.PersistNew(query, decision.Events, cancellationToken);

        return Result<Guid>.Success(paymentId);
    }
}

internal sealed class CreatePaymentDecider
{
    public Decision<Guid> Decide(Guid orderId, decimal amount)
    {
        var paymentId = Guid.CreateVersion7();
        var paymentCreated = new PaymentCreated(paymentId, orderId, amount);

        return Decision<Guid>.Succeed(paymentId, paymentCreated);
    }
}