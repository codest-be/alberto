using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for async reactor processing via consumer registration.
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

    #region Test Reactor - No Base Class!

    public class NotificationReactor :
        IReact<OrderConfirmed>,
        IReact<OrderShipped>
    {
        private readonly List<string> _sentNotifications;

        public NotificationReactor(List<string> sentNotifications)
        {
            _sentNotifications = sentNotifications;
        }

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

    #region Test Setup

    private static (IEventProcessor processor, List<string> notifications, InMemoryCheckpointStore checkpointStore) CreateProcessor()
    {
        var notifications = new List<string>();
        var checkpointStore = new InMemoryCheckpointStore();
        var reactor = new NotificationReactor(notifications);
        var processor = new AsyncReactor<NotificationReactor>(
            reactor, checkpointStore, "notification-reactor-v1");
        return (processor, notifications, checkpointStore);
    }

    #endregion

    #region Handled Event Types

    [Fact]
    public void HandledEventTypes_ShouldContainImplementedTypes()
    {
        var (processor, _, _) = CreateProcessor();

        Assert.Contains("order-confirmed", processor.HandledEventTypes);
        Assert.Contains("order-shipped", processor.HandledEventTypes);
    }

    [Fact]
    public void HandledEventTypes_ShouldNotContainUnimplementedTypes()
    {
        var (processor, _, _) = CreateProcessor();

        Assert.DoesNotContain("order-cancelled", processor.HandledEventTypes);
    }

    #endregion

    #region ProcessBatch Tests

    [Fact]
    public async Task ProcessBatch_ShouldCallHandlers()
    {
        var (processor, notifications, _) = CreateProcessor();

        var orderId = Guid.NewGuid();
        var events = new[]
        {
            CreateEnvelope(new OrderConfirmed(orderId, "test@example.com"), 1),
            CreateEnvelope(new OrderShipped(orderId), 2)
        };

        await processor.ProcessBatchAsync(events);

        Assert.Equal(2, notifications.Count);
        Assert.Contains("Confirmation email", notifications[0]);
        Assert.Contains("Shipping notification", notifications[1]);
    }

    [Fact]
    public async Task ProcessBatch_ShouldSaveCheckpoint()
    {
        var (processor, _, checkpointStore) = CreateProcessor();

        var events = new[]
        {
            CreateEnvelope(new OrderConfirmed(Guid.NewGuid(), "test@example.com"), 10),
            CreateEnvelope(new OrderShipped(Guid.NewGuid()), 15)
        };

        await processor.ProcessBatchAsync(events);

        var checkpoint = await checkpointStore.GetAsync(processor.ProcessorId);
        Assert.Equal(15, checkpoint);
    }

    [Fact]
    public async Task ProcessBatch_ShouldIgnoreUnhandledEvents()
    {
        var (processor, notifications, _) = CreateProcessor();

        var events = new[]
        {
            CreateEnvelope(new OrderConfirmed(Guid.NewGuid(), "test@example.com"), 1),
            CreateEnvelope(new OrderCancelled(Guid.NewGuid()), 2) // Not handled
        };

        await processor.ProcessBatchAsync(events);

        Assert.Single(notifications);
        Assert.Contains("Confirmation email", notifications[0]);
    }

    [Fact]
    public async Task ProcessBatch_EmptyBatch_ShouldNotThrow()
    {
        var (processor, notifications, _) = CreateProcessor();

        var result = await processor.ProcessBatchAsync([]);

        Assert.Equal(ProcessingResult.Continue, result);
        Assert.Empty(notifications);
    }

    [Fact]
    public void ProcessorId_ShouldMatchConstructorArgument()
    {
        var (processor, _, _) = CreateProcessor();

        Assert.Equal("notification-reactor-v1", processor.ProcessorId);
    }

    [Fact]
    public void Constructor_ShouldThrowIfNoReactInterfaces()
    {
        var checkpointStore = new InMemoryCheckpointStore();
        var invalidReactor = new object(); // No IReact<> interfaces

        Assert.Throws<ArgumentException>(() =>
            new AsyncReactor<object>(invalidReactor, checkpointStore, "test"));
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
