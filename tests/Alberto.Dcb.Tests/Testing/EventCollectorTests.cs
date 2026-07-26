using Alberto.Dcb.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Testing;

public class EventCollectorTests
{
    [EventType("collector-probe")]
    private record Probe(string Value) : IEvent;

    [Fact]
    public async Task WaitForProjectedAsync_ReturnsAnEventProjectedAfterTheWaitBegan()
    {
        var collector = new EventCollector();
        var envelope = Envelope();

        var waiting = collector.WaitForProjectedAsync(
            "p1", "collector-probe", ct: TestContext.Current.CancellationToken);

        collector.OnProjected("p1", envelope);

        Assert.Same(envelope, await waiting);
    }

    [Fact]
    public async Task WaitForProjectedAsync_ReturnsAnEventProjectedBeforeTheWaitBegan()
    {
        var collector = new EventCollector();
        var envelope = Envelope();
        collector.OnProjected("p1", envelope);

        Assert.Same(envelope, await collector.WaitForProjectedAsync(
            "p1", "collector-probe", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForProjectedAsync_TimesOutOnTheInjectedClock()
    {
        var time = new FakeTimeProvider();
        var collector = new EventCollector(time);

        var waiting = collector.WaitForProjectedAsync(
            "p1", "never-projected",
            timeout: TimeSpan.FromSeconds(5),
            ct: TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
    }

    private static IEventEnvelope Envelope() => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = new EventType("collector-probe"),
        Tags = Array.Empty<EventTag>(),
        EventData = "{}",
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTime.UtcNow,
    };
}
