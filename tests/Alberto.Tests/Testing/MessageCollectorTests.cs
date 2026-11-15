using Alberto.Messaging;
using Alberto.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class MessageCollectorTests
{
    private static ExternalMessage Message(string type, string? tenant = null) =>
        new(type, "1", "{}", new Dictionary<string, string>()) { TenantId = tenant };

    [Fact]
    public async Task WaitForPublishedAsync_ReturnsAMessagePublishedAfterTheWaitBegan()
    {
        var ct = TestContext.Current.CancellationToken;
        var collector = new MessageCollector();
        var message = Message("order-shipped");

        var waiting = collector.WaitForPublishedAsync("order-shipped", cancellationToken: ct);
        await collector.PublishAsync(message, ct);

        Assert.Same(message, await waiting);
    }

    [Fact]
    public async Task WaitForPublishedAsync_ReturnsAMessagePublishedBeforeTheWaitBegan()
    {
        var ct = TestContext.Current.CancellationToken;
        var collector = new MessageCollector();
        var message = Message("order-shipped");
        await collector.PublishAsync(message, ct);

        Assert.Same(message, await collector.WaitForPublishedAsync("order-shipped", cancellationToken: ct));
    }

    [Fact]
    public async Task WaitForPublishedAsync_ByTenant_IgnoresAnotherTenantsMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var collector = new MessageCollector();
        await collector.PublishAsync(Message("order-shipped", "other"), ct);
        var mine = Message("order-shipped", "mine");
        await collector.PublishAsync(mine, ct);

        Assert.Same(mine, await collector.WaitForPublishedAsync("order-shipped", "mine", cancellationToken: ct));
    }

    [Fact]
    public async Task WaitForPublishedAsync_TimesOutOnTheInjectedClock_NamingWhatItWaitedFor()
    {
        var time = new FakeTimeProvider();
        var collector = new MessageCollector(timeProvider: time);

        var waiting = collector.WaitForPublishedAsync(
            "never-sent", timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => waiting);
        Assert.Contains("never-sent", ex.Message);
    }

    [Fact]
    public async Task PublishAsync_DeliversToTheInnerTransportFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryTransport();
        var collector = new MessageCollector(inner);

        await collector.PublishAsync(Message("order-shipped"), ct);

        Assert.Single(inner.Published);
        Assert.Single(collector.Published);
    }

    [Fact]
    public async Task PublishAsync_DoesNotRecordAMessageTheInnerTransportRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var collector = new MessageCollector(new FailingTransport());

        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.PublishAsync(Message("order-shipped"), ct));

        Assert.Empty(collector.Published);
    }

    private sealed class FailingTransport : IMessageTransport
    {
        public Task PublishAsync(ExternalMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("broker down");
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
