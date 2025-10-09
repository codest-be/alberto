using Alberto.EventStore.Events;

namespace Alberto.Example.Modules.Orders.Events;

[EventType("order-created")]
public record OrderCreated([property: Tag(Tags.Order)] Guid OrderId, decimal Amount, string CustomerId);