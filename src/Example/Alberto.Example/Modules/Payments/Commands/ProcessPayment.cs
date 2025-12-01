using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Payments.Enums;
using Alberto.Example.Modules.Payments.Events;

namespace Alberto.Example.Modules.Payments.Commands;

public sealed record ProcessPaymentCommand(Guid PaymentId) : ICommand;

public sealed class ProcessPaymentHandler(PaymentEventStore eventStore)
    : ICommandHandler<ProcessPaymentCommand>
{
    public async Task<Result> Handle(ProcessPaymentCommand command, CancellationToken cancellationToken = default)
    {
        var decider = new ProcessPaymentDecider();
        var query = ProcessPaymentDecider.GetQuery(command.PaymentId);

        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = decider.Evolve(events);
        var decision = decider.Decide(state, command.PaymentId);

        if (decision.IsError)
            return Result.Fail(decision.Problems.First());

        await eventStore.Persist(query, lastEventId, decision.Events, cancellationToken);

        return Result.Success();
    }
}

internal sealed record ProcessPaymentState
{
    public bool Exists { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.Draft;
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class ProcessPaymentDecider : IProjector<ProcessPaymentState>
{
    public ProcessPaymentState Apply(ProcessPaymentState state, object @event)
    {
        return @event switch
        {
            PaymentCreated e => state with { Exists = true, Status = PaymentStatus.Created, OrderId = e.OrderId, Amount = e.Amount },
            PaymentProcessed => state with { Status = PaymentStatus.Processed },
            _ => state
        };
    }

    public static StreamQuery GetQuery(Guid paymentId)
    {
        return new StreamQuery([new EventTag(Tags.Payment, paymentId.ToString())])
            .WithEventType<PaymentCreated>()
            .WithEventType<PaymentProcessed>();
    }

    public Decision Decide(ProcessPaymentState state, Guid paymentId)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.PaymentNotFound(paymentId));

        if (state.Status == PaymentStatus.Processed)
            return Decision.Fail(PaymentProblems.PaymentAlreadyProcessed());

        if (state.Status != PaymentStatus.Created)
            return Decision.Fail(PaymentProblems.InvalidStatusForProcessing(state.Status));

        var paymentProcessed = new PaymentProcessed(paymentId);
        return Decision.Succeed(paymentProcessed);
    }
}