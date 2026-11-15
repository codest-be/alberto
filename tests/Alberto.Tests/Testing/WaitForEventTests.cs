using Alberto.InMemory;
using Alberto.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class WaitForEventTests
{
    private static EventToPersist Event(string type, string data = "{}") =>
        new() { EventType = new EventType(type), Tags = [], EventData = data };

    [Fact]
    public async Task ReturnsAnEventAppendedAfterTheWaitBegan()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EventStore(new InMemoryEventStoreBackend());

        var waiting = store.WaitForEventAsync(DcbQuery.ByTypes(["order-shipped"]), "the order shipped", cancellationToken: ct);
        await store.AppendAsync([Event("order-placed"), Event("order-shipped")], cancellationToken: ct);

        Assert.Equal("order-shipped", (await waiting).EventType.Id);
    }

    [Fact]
    public async Task ReturnsAnEventAlreadyInTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EventStore(new InMemoryEventStoreBackend());
        var appended = await store.AppendAsync([Event("order-shipped")], cancellationToken: ct);

        var found = await store.WaitForEventAsync(DcbQuery.ByTypes(["order-shipped"]), "the order shipped", cancellationToken: ct);

        Assert.Equal(appended.Single().GlobalPosition, found.GlobalPosition);
    }

    [Fact]
    public async Task SkipsMatchesThePredicateRejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EventStore(new InMemoryEventStoreBackend());
        await store.AppendAsync([Event("order-shipped"), Event("order-shipped")], cancellationToken: ct);

        var found = await store.WaitForEventAsync(
            DcbQuery.ByTypes(["order-shipped"]), "the second shipment", e => e.GlobalPosition > 1, cancellationToken: ct);

        Assert.Equal(2, found.GlobalPosition);
    }

    [Fact]
    public async Task TimesOutOnTheInjectedClock_NamingWhatItWaitedFor()
    {
        var time = new FakeTimeProvider();
        var store = new EventStore(new InMemoryEventStoreBackend());

        var waiting = store.WaitForEventAsync(
            DcbQuery.ByTypes(["never-appended"]), "the event that never comes",
            timeout: TimeSpan.FromSeconds(5), timeProvider: time, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => waiting);
        Assert.Contains("the event that never comes", ex.Message);
    }
}
