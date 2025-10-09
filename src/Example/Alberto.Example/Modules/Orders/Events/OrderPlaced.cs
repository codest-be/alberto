using Alberto.EventStore.Events;

namespace Alberto.Example.Modules.Orders.Events;

[EventType("order-placed")]
public record OrderPlaced([property: Tag(Tags.Order)] Guid OrderId, decimal Amount, string CustomerId);