using Alberto.Messaging;
using Alberto.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class InMemoryOutboxStoreTests
{
    [Fact]
    public async Task ClaimPendingAsync_DoesNotHandOutAnEntryWhoseLeaseIsStillHeld()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryOutboxStore(time);
        var ct = TestContext.Current.CancellationToken;
        await store.InsertAsync(NewEntry(), ct);

        var first = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-a", ct);
        Assert.Single(first);

        time.Advance(TimeSpan.FromSeconds(30));
        var second = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-b", ct);

        Assert.Empty(second);
    }

    [Fact]
    public async Task ClaimPendingAsync_ReclaimsAnEntryWhoseLeaseExpired()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryOutboxStore(time);
        var ct = TestContext.Current.CancellationToken;
        await store.InsertAsync(NewEntry(), ct);

        await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-a", ct);
        time.Advance(TimeSpan.FromMinutes(2));

        // This is the whole point of the lease, and it is what strands rows when it is
        // missing: a relay that dies between claiming and marking leaves the entry claimed
        // forever otherwise.
        Assert.Single(await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-b", ct));
    }

    [Fact]
    public async Task MarkDeliveredAsync_RejectsASupersededClaim()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryOutboxStore(time);
        var ct = TestContext.Current.CancellationToken;
        await store.InsertAsync(NewEntry(), ct);

        var stale = (await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-a", ct))[0];
        time.Advance(TimeSpan.FromMinutes(2));
        await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-b", ct);

        Assert.False(await store.MarkDeliveredAsync(stale, ct));
    }

    private static OutboxEntry NewEntry() =>
        new(
            Id: Guid.NewGuid(),
            SourceEventId: Guid.NewGuid(),
            MessageType: "order.placed",
            Version: "1",
            Payload: "{}",
            Metadata: new Dictionary<string, string>(),
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null);
}
