using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Unit tests for <see cref="EventAppendedSignal"/> — the coalescing async
/// auto-reset event used to wake <see cref="EventStoreHead"/> on append.
/// </summary>
public sealed class EventAppendedSignalTests
{
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Signal_BeforeWait_CompletesImmediately()
    {
        var signal = new EventAppendedSignal();
        signal.Signal();

        // Already signalled → returns without blocking.
        await signal.WaitAsync(LongTimeout, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Signal_WhileWaiting_CompletesWait()
    {
        var signal = new EventAppendedSignal();
        var wait = signal.WaitAsync(LongTimeout, TestContext.Current.CancellationToken);

        Assert.False(wait.IsCompleted);
        signal.Signal();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Wait_AutoResets_SecondWaitBlocksUntilNextSignal()
    {
        var signal = new EventAppendedSignal();

        signal.Signal();
        await signal.WaitAsync(LongTimeout, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // The signal was consumed — a fresh wait must not complete on its own.
        var second = signal.WaitAsync(LongTimeout, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        signal.Signal();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MultipleSignals_CoalesceIntoOne()
    {
        var signal = new EventAppendedSignal();

        signal.Signal();
        signal.Signal();
        signal.Signal();

        // First wait consumes the coalesced signal.
        await signal.WaitAsync(LongTimeout, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // No signal remains pending.
        var next = signal.WaitAsync(LongTimeout, TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted);
    }

    [Fact]
    public async Task Wait_TimesOut_CompletesWithoutSignal()
    {
        var signal = new EventAppendedSignal();

        // No signal — the wait completes on its (short) timeout, not by cancellation.
        await signal.WaitAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Wait_HonorsCancellation()
    {
        var signal = new EventAppendedSignal();
        using var cts = new CancellationTokenSource();

        var wait = signal.WaitAsync(LongTimeout, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
