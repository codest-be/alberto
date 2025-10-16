using Alberto.EventSourcing.Projections;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Example.Modules.Payments.Enums;
using Alberto.Example.Modules.Payments.Events;
using Alberto.Projections;

namespace Alberto.Example.Modules.Payments.Projections;

[Subscription("payment-projection")]
public class PaymentProjectionSubscription(
    ProjectionHandler<Guid, Payment> handler) :
    IHandleEvent<PaymentCreated>,
    IHandleEvent<PaymentProcessed>
{
    public ValueTask Handle(PaymentCreated @event, EventContext context, CancellationToken cancellationToken = default)
        => handler.Handle(@event.PaymentId, @event, context, cancellationToken);

    public ValueTask Handle(PaymentProcessed @event, EventContext context, CancellationToken cancellationToken = default)
        => handler.Handle(@event.PaymentId, @event, context, cancellationToken);
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
            PaymentCreated e => projectionState with { PaymentId = e.PaymentId, OrderId = e.OrderId, Amount = e.Amount, Status = PaymentStatus.Created },
            PaymentProcessed => projectionState with { Status = PaymentStatus.Processed },
            _ => projectionState
        };
    }
}