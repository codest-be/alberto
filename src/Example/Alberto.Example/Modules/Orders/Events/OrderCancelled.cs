using Alberto.EventStore.Events;

namespace Alberto.Example.Modules.Orders.Events;

[EventType("order-cancelled")]
public record OrderCancelled([property: Tag(Tags.Order)] Guid OrderId, string Reason);