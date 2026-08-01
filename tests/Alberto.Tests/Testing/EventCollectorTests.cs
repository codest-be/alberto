using Alberto.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class EventCollectorTests
{
    [EventType("collector-probe")]
    private record Probe(string Value) : IEvent;

    [Fact]
    public async Task WaitForProjectedAsync_ReturnsAnEventProjectedAfterTheWaitBegan()
    {
        using var collector = new EventCollector();
        var envelope = Envelope();

        var waiting = collector.WaitForProjectedAsync(
            "p1", "collector-probe", ct: TestContext.Current.CancellationToken);

        collector.OnProjected("p1", envelope);

        Assert.Same(envelope, await waiting);
    }

    [Fact]
    public async Task WaitForProjectedAsync_ReturnsAnEventProjectedBeforeTheWaitBegan()
    {
        using var collector = new EventCollector();
        var envelope = Envelope();
        collector.OnProjected("p1", envelope);

        Assert.Same(envelope, await collector.WaitForProjectedAsync(
            "p1", "collector-probe", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForProjectedAsync_TimesOutOnTheInjectedClock()
    {
        var time = new FakeTimeProvider();
        using var collector = new EventCollector(time);

        var waiting = collector.WaitForProjectedAsync(
            "p1", "never-projected",
            timeout: TimeSpan.FromSeconds(5),
            ct: TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
    }

    [Fact]
    public async Task WaitForProjectedAsync_ThrowsOperationCanceledWhenTokenFires()
    {
        using var collector = new EventCollector();
        using var cts = new CancellationTokenSource();

        // Start waiting for an event that will never arrive.
        var waiting = collector.WaitForProjectedAsync(
            "p1", "never-projected",
            timeout: TimeSpan.FromSeconds(30),
            ct: cts.Token);

        // Cancel while the wait is in progress.
        await cts.CancelAsync();

        // Must surface as OperationCanceledException (or its subtype TaskCanceledException),
        // not as TimeoutException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
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
