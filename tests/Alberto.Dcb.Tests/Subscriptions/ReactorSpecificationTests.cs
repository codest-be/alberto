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

    public class NotificationReactor(List<string> sentNotifications) :
        IReact<OrderConfirmed>,
        IReact<OrderShipped>
    {
        public Task ReactAsync(OrderConfirmed @event, ReactorContext context, CancellationToken ct)
        {
            sentNotifications.Add($"Confirmation email sent to {@event.CustomerEmail} for order {@event.OrderId}");
            return Task.CompletedTask;
        }

        public Task ReactAsync(OrderShipped @event, ReactorContext context, CancellationToken ct)
        {
            sentNotifications.Add($"Shipping notification for order {@event.OrderId}");
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Test Setup

    private static (IEventProcessor processor, List<string> notifications) CreateProcessor()
    {
        var notifications = new List<string>();
        var reactor = new NotificationReactor(notifications);
        var processor = new AsyncReactor<NotificationReactor>(reactor, "notification-reactor-v1");
        return (processor, notifications);
    }

    #endregion

    #region Handled Event Types

    [Fact]
    public void HandledEventTypes_ShouldContainImplementedTypes()
    {
        var (processor, _) = CreateProcessor();

        Assert.Contains("order-confirmed", processor.HandledEventTypes);
        Assert.Contains("order-shipped", processor.HandledEventTypes);
    }

    [Fact]
    public void HandledEventTypes_ShouldNotContainUnimplementedTypes()
    {
        var (processor, _) = CreateProcessor();

        Assert.DoesNotContain("order-cancelled", processor.HandledEventTypes);
    }

    #endregion

    #region ProcessEvent Tests

    [Fact]
    public async Task ProcessEvent_ShouldCallHandlers()
    {
        var (processor, notifications) = CreateProcessor();

        var orderId = Guid.NewGuid();

        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderConfirmed(orderId, "test@example.com"), 1),
            TestContext.Current.CancellationToken);

        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderShipped(orderId), 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, notifications.Count);
        Assert.Contains("Confirmation email", notifications[0]);
        Assert.Contains("Shipping notification", notifications[1]);
    }

    [Fact]
    public async Task ProcessEvent_ShouldIgnoreUnhandledEvents()
    {
        var (processor, notifications) = CreateProcessor();

        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderConfirmed(Guid.NewGuid(), "test@example.com"), 1),
            TestContext.Current.CancellationToken);

        await processor.ProcessEventAsync(
            CreateEnvelope(new OrderCancelled(Guid.NewGuid()), 2), // Not handled
            TestContext.Current.CancellationToken);

        Assert.Single(notifications);
        Assert.Contains("Confirmation email", notifications[0]);
    }

    [Fact]
    public void ProcessorId_ShouldMatchConstructorArgument()
    {
        var (processor, _) = CreateProcessor();

        Assert.Equal("notification-reactor-v1", processor.ProcessorId);
    }

    [Fact]
    public void Constructor_ShouldThrowIfNoReactInterfaces()
    {
        var invalidReactor = new object(); // No IReact<> interfaces

        Assert.Throws<ArgumentException>(() =>
            new AsyncReactor<object>(invalidReactor, "test"));
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
