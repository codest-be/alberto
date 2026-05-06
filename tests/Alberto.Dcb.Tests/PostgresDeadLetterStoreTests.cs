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
    public async Task ClaimRetryRequestedAsync_ShouldWorkAgainstSingleTenantSchema()
    {
        var (deadLetterStore, entry, tag) = await SeedRetryRequestedEntryAsync();

        var retries = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "test-worker",
            ct: TestContext.Current.CancellationToken);

        var retry = Assert.Single(retries);
        Assert.Null(retry.TenantId);
        Assert.Contains(tag.Value, retry.Tags ?? []);
        Assert.NotNull(retry.CreatedAt);
        Assert.NotNull(retry.ClaimedAt);
        Assert.NotNull(retry.ClaimExpiresAt);
        Assert.Equal("test-worker", retry.ClaimedBy);
    }

    [Fact]
    public async Task ClaimRetryRequestedAsync_ShouldNotReturnAlreadyClaimedEntry()
    {
        var (deadLetterStore, entry, _) = await SeedRetryRequestedEntryAsync();

        var first = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-1",
            ct: TestContext.Current.CancellationToken);
        Assert.Single(first);

        // Second call before the lease expires should return nothing.
        var second = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-2",
            ct: TestContext.Current.CancellationToken);
        Assert.Empty(second);
    }

    [Fact]
    public async Task ClaimRetryRequestedAsync_ShouldReclaimAfterLeaseExpires()
    {
        var (deadLetterStore, entry, _) = await SeedRetryRequestedEntryAsync();

        // Claim with an already-expired lease (negative-ish: a 1ms lease that we sleep past).
        var first = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMilliseconds(1),
            claimedBy: "worker-1",
            ct: TestContext.Current.CancellationToken);
        Assert.Single(first);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Lease has expired — a different worker should be able to re-claim.
        var second = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-2",
            ct: TestContext.Current.CancellationToken);
        var reclaimed = Assert.Single(second);
        Assert.Equal("worker-2", reclaimed.ClaimedBy);
    }

    [Fact]
    public async Task AbandonRetryAsync_ShouldPreventReclaimUntilReMarked()
    {
        var (deadLetterStore, entry, _) = await SeedRetryRequestedEntryAsync();

        var first = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-1",
            ct: TestContext.Current.CancellationToken);
        var claimed = Assert.Single(first);

        await deadLetterStore.AbandonRetryAsync(claimed.Id, TestContext.Current.CancellationToken);

        // Abandoning clears retry_requested, so the row is no longer eligible.
        var second = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-2",
            ct: TestContext.Current.CancellationToken);
        Assert.Empty(second);

        // Re-marking puts it back in the retry pool.
        await deadLetterStore.MarkForRetryAsync(entry.ProcessorId, TestContext.Current.CancellationToken);
        var third = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-2",
            ct: TestContext.Current.CancellationToken);
        var reclaimed = Assert.Single(third);
        Assert.Equal("worker-2", reclaimed.ClaimedBy);
    }

    private async Task<(PostgresDeadLetterStore Store, DeadLetterEntry Entry, EventTag Tag)> SeedRetryRequestedEntryAsync()
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
            ProcessorId: $"processor-{Guid.NewGuid():N}",
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

        return (deadLetterStore, entry, tag);
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
