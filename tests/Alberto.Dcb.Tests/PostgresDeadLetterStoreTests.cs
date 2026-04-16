using System.Text.Json;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests;

public sealed class PostgresDeadLetterStoreTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    [EventType("order-created")]
    public sealed record OrderCreated(Guid OrderId) : IEvent;

    [Fact]
    public async Task GetRetryRequestedWithLockAsync_ShouldWorkAgainstSingleTenantSchema()
    {
        var eventStore = new PostgresEventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var deadLetterStore = new PostgresDeadLetterStore(fixture.DataSource);

        var orderId = Guid.NewGuid();
        var tag = new EventTag("order", orderId.ToString());
        var appended = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId), tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        var persisted = appended.Single();
        var entry = new DeadLetterEntry(
            Id: Guid.NewGuid(),
            ProcessorId: "processor-1",
            EventId: persisted.Id,
            EventType: persisted.EventType.Id,
            EventData: persisted.EventData,
            ErrorMessage: "boom",
            StackTrace: null,
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow,
            GlobalPosition: persisted.GlobalPosition);

        await deadLetterStore.StoreAsync(entry, TestContext.Current.CancellationToken);
        await deadLetterStore.MarkForRetryAsync(entry.ProcessorId, TestContext.Current.CancellationToken);

        var retries = await deadLetterStore.GetRetryRequestedWithLockAsync(
            entry.ProcessorId,
            ct: TestContext.Current.CancellationToken);

        var retry = Assert.Single(retries);
        Assert.Null(retry.TenantId);
        Assert.Contains(tag.Value, retry.Tags ?? []);
        Assert.NotNull(retry.CreatedAt);
    }

    private static EventToPersist CreateEvent<TEvent>(TEvent @event, EventTag? tag = null)
        where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = tag.HasValue ? [tag.Value] : [],
            EventData = JsonSerializer.Serialize(@event),
        };
    }
}
