using System.Text.Json;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests;

[Trait("Category", "Integration")]
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
        Assert.Null(retry.Entry.TenantId);
        Assert.Contains(tag.Value, retry.Entry.Tags ?? []);
        Assert.NotNull(retry.Entry.CreatedAt);
        Assert.NotNull(retry.Entry.ClaimedAt);
        Assert.NotNull(retry.Entry.ClaimExpiresAt);
        Assert.Equal("test-worker", retry.Entry.ClaimedBy);
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

        // Wall-clock, deliberately: the claim expiry is evaluated by PostgreSQL's now(),
        // which no client-side TimeProvider can move. Advancing a fake clock here would
        // read as determinism that is not there.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Lease has expired — a different worker should be able to re-claim.
        var second = await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-2",
            ct: TestContext.Current.CancellationToken);
        var reclaimed = Assert.Single(second);
        Assert.Equal("worker-2", reclaimed.Entry.ClaimedBy);
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

        Assert.True(await deadLetterStore.AbandonRetryAsync(claimed, TestContext.Current.CancellationToken));

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
        Assert.Equal("worker-2", reclaimed.Entry.ClaimedBy);
    }

    [Fact]
    public async Task StaleClaim_CannotCompleteOrAbandonANewerClaim()
    {
        var (deadLetterStore, entry, _) = await SeedRetryRequestedEntryAsync();
        var ct = TestContext.Current.CancellationToken;

        var first = Assert.Single(await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 1,
            leaseDuration: TimeSpan.FromMilliseconds(1),
            claimedBy: "stale-worker",
            ct: ct));

        // Wall-clock, deliberately: the claim expiry is evaluated by PostgreSQL's now(),
        // which no client-side TimeProvider can move. Advancing a fake clock here would
        // read as determinism that is not there.
        await Task.Delay(50, ct);

        var current = Assert.Single(await deadLetterStore.ClaimRetryRequestedAsync(
            entry.ProcessorId,
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "current-worker",
            ct: ct));

        Assert.NotEqual(first.Token, current.Token);
        Assert.False(await deadLetterStore.CompleteRetryAsync(first, ct));
        Assert.False(await deadLetterStore.AbandonRetryAsync(first, ct));
        Assert.Equal(1, await deadLetterStore.CountAsync(entry.ProcessorId, ct));

        Assert.True(await deadLetterStore.CompleteRetryAsync(current, ct));
        Assert.Equal(0, await deadLetterStore.CountAsync(entry.ProcessorId, ct));
    }

    [Fact]
    public async Task ExplicitMultiTenantExpectation_RejectsSingleTenantSchema()
    {
        var store = new PostgresDeadLetterStore(fixture.DataSource, multiTenant: true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CountAsync(
                $"processor-{Guid.NewGuid():N}",
                TestContext.Current.CancellationToken));

        Assert.Contains("tenancy mismatch", failure.Message);
        Assert.Contains("single-tenant", failure.Message);
    }

    private async Task<(PostgresDeadLetterStore Store, DeadLetterEntry Entry, EventTag Tag)> SeedRetryRequestedEntryAsync()
    {
        var eventStore = new EventStore(new PostgresEventStoreBackend(fixture.DataSource));
        var deadLetterStore = new PostgresDeadLetterStore(fixture.DataSource);

        var orderId = Guid.NewGuid();
        var tag = new EventTag("order", orderId.ToString());
        var appended = await eventStore.AppendAsync(
            [CreateEvent(new OrderCreated(orderId), tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        var persisted = appended.Single();
        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            ProcessorId = $"processor-{Guid.NewGuid():N}",
            EventId = persisted.Id,
            EventType = persisted.EventType.Id,
            EventData = persisted.EventData,
            ErrorMessage = "boom",
            StackTrace = null,
            AttemptCount = 1,
            FailedAt = DateTimeOffset.UtcNow,
            GlobalPosition = persisted.GlobalPosition,
        };

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
