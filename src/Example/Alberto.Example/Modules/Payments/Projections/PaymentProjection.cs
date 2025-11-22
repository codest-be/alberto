using Alberto.EventSourcing.Projections;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Example.Modules.Payments.Enums;
using Alberto.Example.Modules.Payments.Events;
using Alberto.Projections;

namespace Alberto.Example.Modules.Payments.Projections;

[Subscription("payment-projection")]
public class PaymentProjectionSubscription(
    IProjectionRepository<Guid, Payment> repository,
    PaymentProjector projector) :
    IProjectionSubscription<Guid, Payment>,
    IHandleEvent<PaymentCreated>,
    IHandleEvent<PaymentProcessed>
{
    // Handle methods can be empty - EventRouter routes via IProjectionSubscription
    public ValueTask Handle(PaymentCreated @event, EventContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask Handle(PaymentProcessed @event, EventContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public IProjector<Payment> Projector => projector;
    public IProjectionRepository<Guid, Payment> Repository => repository;

    public Guid GetKey(object @event) => @event switch
    {
        PaymentCreated e => e.PaymentId,
        PaymentProcessed e => e.PaymentId,
        _ => throw new InvalidOperationException($"Unsupported event type: {@event.GetType().Name}")
    };
}

public record Payment
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.Draft;
}

[GenerateMigration(Schema = "payments", TableName = "payment")]
public class PaymentProjector : IProjector<Payment>
{
    public Payment Apply(Payment projectionState, object @event)
    {
        return @event switch
        {
            PaymentCreated e => projectionState with
            {
                PaymentId = e.PaymentId, OrderId = e.OrderId, Amount = e.Amount, Status = PaymentStatus.Created
            },
            PaymentProcessed => projectionState with { Status = PaymentStatus.Processed },
            _ => projectionState
        };
    }
}