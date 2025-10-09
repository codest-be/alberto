using Alberto.EventStore.Events;

namespace Alberto.Example.Modules.Orders.Events;

[EventType("order-shipped")]
public record OrderShipped([property: Tag(Tags.Order)] Guid OrderId, string TrackingNumber);