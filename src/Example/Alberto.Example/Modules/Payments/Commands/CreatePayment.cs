using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Payments.Events;

namespace Alberto.Example.Modules.Payments.Commands;

public sealed record CreatePaymentCommand(Guid OrderId, decimal Amount) : ICommand<Guid>;

public sealed class CreatePaymentHandler(PaymentEventStore eventStore)
    : ICommandHandler<CreatePaymentCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreatePaymentCommand command, CancellationToken cancellationToken = default)
    {
        var decision = new CreatePaymentDecider().Decide(command.OrderId, command.Amount);

        var paymentId = decision.Value;
        var query = new StreamQuery([new EventTag(Tags.Payment, paymentId.ToString())]);

        await eventStore.PersistNew(decision.Events, cancellationToken);

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