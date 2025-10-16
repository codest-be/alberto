using Alberto.EventStore.Events;

namespace Alberto.Example.Modules.Payments.Events;

[EventType("payment-processed")]
public record PaymentProcessed([property: Tag(Tags.Payment)] Guid PaymentId);