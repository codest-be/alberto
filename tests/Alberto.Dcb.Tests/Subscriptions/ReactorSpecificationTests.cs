using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for the Reactor base class.
/// </summary>
public class ReactorSpecificationTests
{
    #region Test Events

    [EventType("order-confirmed")]
    public record OrderConfirmed(Guid OrderId, string CustomerEmail) : IEvent;

    [EventType("order-shipped")]
    public record OrderShipped(Guid OrderId) : IEvent;

    [EventType("order-cancelled")]
    public record OrderCancelled(Guid OrderId) : IEvent;

    #endregion

    #region Test Reactor

    public class NotificationReactor : Reactor,
        IReact<OrderConfirmed>,
        IReact<OrderShipped>
    {
        private readonly List<string> _sentNotifications;

        public NotificationReactor(ICheckpointStore checkpointStore, List<string> sentNotifications)
            : base(checkpointStore)
        {
            _sentNotifications = sentNotifications;
        }

        public override string ProcessorId => "notification-reactor-v1";

        public Task ReactAsync(OrderConfirmed @event, IEventEnvelope envelope, CancellationToken ct)
        {
            _sentNotifications.Add($"Confirmation email sent to {@event.CustomerEmail} for order {@event.OrderId}");
            return Task.CompletedTask;
        }

        public Task ReactAsync(OrderShipped @event, IEventEnvelope envelope, CancellationToken ct)
        {
            _sentNotifications.Add($"Shipping notification for order {@event.OrderId}");
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Handled Event Types

    [Fact]
    public void HandledEventTypes_ShouldContainImplementedTypes()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var notifications = new List<string>();
        var reactor = new NotificationReactor(checkpointStore, notifications);

        Assert.Contains("order-confirmed", reactor.HandledEventTypes);
        Assert.Contains("order-shipped", reactor.HandledEventTypes);
    }

    [Fact]
    public void HandledEventTypes_ShouldNotContainUnimplementedTypes()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var notifications = new List<string>();
        var reactor = new NotificationReactor(checkpointStore, notifications);

        Assert.DoesNotContain("order-cancelled", reactor.HandledEventTypes);
    }

    #endregion

    #region ProcessBatch Tests

    [Fact]
    public async Task ProcessBatch_ShouldCallHandlers()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var notifications = new List<string>();
        var reactor = new NotificationReactor(checkpointStore, notifications);

        var orderId = Guid.NewGuid();
        var events = new[]
        {
            CreateEnvelope(new OrderConfirmed(orderId, "test@example.com"), 1),
            CreateEnvelope(new OrderShipped(orderId), 2)
        };

        await reactor.ProcessBatchAsync(events);

        Assert.Equal(2, notifications.Count);
        Assert.Contains("Confirmation email", notifications[0]);
        Assert.Contains("Shipping notification", notifications[1]);
    }

    [Fact]
    public async Task ProcessBatch_ShouldSaveCheckpoint()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var notifications = new List<string>();
        var reactor = new NotificationReactor(checkpointStore, notifications);

        var events = new[]
        {
            CreateEnvelope(new OrderConfirmed(Guid.NewGuid(), "test@example.com"), 10),
            CreateEnvelope(new OrderShipped(Guid.NewGuid()), 15)
        };

        await reactor.ProcessBatchAsync(events);

        var checkpoint = await checkpointStore.GetAsync(reactor.ProcessorId);
        Assert.Equal(15, checkpoint);
    }

    [Fact]
    public async Task ProcessBatch_ShouldIgnoreUnhandledEvents()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var notifications = new List<string>();
        var reactor = new NotificationReactor(checkpointStore, notifications);

        var events = new[]
        {
            CreateEnvelope(new OrderConfirmed(Guid.NewGuid(), "test@example.com"), 1),
            CreateEnvelope(new OrderCancelled(Guid.NewGuid()), 2) // Not handled
        };

        await reactor.ProcessBatchAsync(events);

        Assert.Single(notifications);
        Assert.Contains("Confirmation email", notifications[0]);
    }

    [Fact]
    public async Task ProcessBatch_EmptyBatch_ShouldNotThrow()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var notifications = new List<string>();
        var reactor = new NotificationReactor(checkpointStore, notifications);

        var result = await reactor.ProcessBatchAsync([]);

        Assert.Equal(ProcessingResult.Continue, result);
        Assert.Empty(notifications);
    }

    #endregion

    #region Helper Methods

    private static IEventEnvelope CreateEnvelope<TEvent>(TEvent @event, long position) where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));

        return new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            GlobalPosition = position,
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event),
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };
    }

    #endregion
}
