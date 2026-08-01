using Alberto.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class PollTests
{
    [Fact]
    public async Task UntilAsync_ReturnsAsSoonAsTheConditionHolds()
    {
        var calls = 0;

        await Poll.UntilAsync(
            () => ++calls >= 3,
            "the third call",
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task UntilAsync_ThrowsTimeoutNamingWhatItWasWaitingFor()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Poll.UntilAsync(
            () => false,
            "a condition that never holds",
            timeout: TimeSpan.FromMilliseconds(50),
            interval: TimeSpan.FromMilliseconds(1),
            ct: TestContext.Current.CancellationToken));

        // Assertion-library-neutral by contract: the package must not reference xunit,
        // so a timeout is an exception, never an Assert.Fail.
        Assert.Contains("a condition that never holds", ex.Message);
    }

    [Fact]
    public async Task UntilAsync_EvaluatesTheConditionOnceBeforeWaitingAtAll()
    {
        // A condition already true must not cost an interval. Tests that poll for
        // work already done are the bulk of the suite's wall-clock time.
        await Poll.UntilAsync(
            () => true,
            "an already-true condition",
            timeout: TimeSpan.FromMilliseconds(1),
            interval: TimeSpan.FromMinutes(1),
            ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UntilAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Poll.UntilAsync(
            () => false,
            "anything",
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            ct: cts.Token));
    }
}
