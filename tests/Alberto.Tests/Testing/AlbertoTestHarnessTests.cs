using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class AlbertoTestHarnessTests
{
    [EventType("harness-order-created")]
    private record HarnessOrderCreated(Guid OrderId, decimal Amount) : IEvent;

    [Fact]
    public async Task AppendThenWaitForQuiescence_LetsAProjectionBeAssertedWithoutPolling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var stateStore = new InMemoryStateStore<OrderTotal>();

        await using var harness = await AlbertoTestHarness.StartAsync(
            "orders",
            module => module
                .WithInMemory()
                .AddProjection(
                    DeclareProjection.For<OrderTotal>("order-total")
                        .On<HarnessOrderCreated>(
                            id: e => e.OrderId.ToString(),
                            apply: (state, e, ctx) => new OrderTotal
                            {
                                OrderId = e.OrderId,
                                Amount = e.Amount
                            })
                        .Build(),
                    _ => _ => stateStore),
            cancellationToken: cancellationToken);

        await harness.AppendAsync(new HarnessOrderCreated(orderId, 42m), cancellationToken: cancellationToken);
        await harness.WaitForQuiescenceAsync(cancellationToken: cancellationToken);

        var loaded = await stateStore.LoadManyAsync([orderId.ToString()], cancellationToken);
        Assert.Equal(42m, loaded[orderId.ToString()].Amount);
    }

    [Fact]
    public async Task WaitForQuiescenceAsync_ThrowsRatherThanReturningWhenNothingCatchesUp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var harness = await AlbertoTestHarness.StartAsync(
            "stalled",
            module => module
                .WithInMemory()
                .ReactTo<HarnessOrderCreated>(
                    sp => (e, stalledCt) => Task.Delay(Timeout.Infinite, stalledCt),
                    processorId: "stalled-reactor"),
            cancellationToken: cancellationToken);

        await harness.AppendAsync(new HarnessOrderCreated(Guid.NewGuid(), 1m), cancellationToken: cancellationToken);

        // A harness that returned silently here would turn every downstream assertion into a
        // race that fails somewhere else, days later.
        await Assert.ThrowsAsync<TimeoutException>(
            () => harness.WaitForQuiescenceAsync(TimeSpan.FromMilliseconds(200), cancellationToken));
    }
}
