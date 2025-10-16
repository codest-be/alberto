using Alberto.EventStore.Events;

namespace Alberto.Example.Modules.Payments.Events;

[EventType("payment-created")]
public record PaymentCreated([property: Tag(Tags.Payment)] Guid PaymentId, Guid OrderId, decimal Amount);