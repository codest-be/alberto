using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// The read-your-writes primitive: wait until a processor has consumed up to a position,
/// instead of sleeping for a guess.
/// </summary>
public sealed class ProjectionCatchUpTests
{
    private const string Processor = "catchup-projection";

    [Fact]
    public async Task WaitForProjection_ReturnsOnceTheProcessorReachesThePosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var checkpoints = new InMemoryCheckpointStore();
        var catchUp = new ProjectionCatchUp(new InMemoryEventStoreBackend(), checkpoints);

        var waiting = catchUp.WaitForProjectionAsync(
            Processor, position: 7, TimeSpan.FromSeconds(10), ct);

        // Nothing has consumed anything yet, so the wait cannot have finished.
        waiting.IsCompleted.Should().BeFalse(
            because: "the processor has not reached position 7");

        await checkpoints.SaveAsync(Processor, 7, ct);

        await waiting;
    }

    [Fact]
    public async Task WaitForProjection_ReturnsWhenTheProcessorIsAlreadyPastThePosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var checkpoints = new InMemoryCheckpointStore();
        await checkpoints.SaveAsync(Processor, 12, ct);

        var catchUp = new ProjectionCatchUp(new InMemoryEventStoreBackend(), checkpoints);

        // A timeout of zero leaves no room to poll: this can only pass by returning on the
        // first look, which is what a caller reading a projection it already caught up with
        // should pay.
        await catchUp.WaitForProjectionAsync(Processor, position: 7, TimeSpan.Zero, ct);
    }

    [Fact]
    public async Task WaitForProjection_ThrowsNamingTheProcessorWhenItDoesNotCatchUpInTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var checkpoints = new InMemoryCheckpointStore();
        await checkpoints.SaveAsync(Processor, 3, ct);

        var catchUp = new ProjectionCatchUp(new InMemoryEventStoreBackend(), checkpoints);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            catchUp.WaitForProjectionAsync(
                Processor, position: 9, TimeSpan.FromMilliseconds(100), ct));

        // Whoever hits this in production has a stopped, faulted or out-of-process loop, and
        // needs to know which processor and how far behind it was without attaching a debugger.
        thrown.Message.Should().Contain(Processor).And.Contain("9").And.Contain("3");
    }

    [Fact]
    public async Task WaitForProjection_WithoutAPosition_WaitsForTheHeadAsOfTheCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var checkpoints = new InMemoryCheckpointStore();

        // A store somebody else keeps writing to. Read the head twice and it is already higher,
        // so an implementation that re-reads it is chasing a target it can never reach — the
        // one thing a read-your-writes wait must not do.
        var backend = new GrowingHeadBackend();
        var catchUp = new ProjectionCatchUp(backend, checkpoints);

        var waiting = catchUp.WaitForProjectionAsync(Processor, TimeSpan.FromSeconds(10), ct);

        await Task.Delay(50, ct);
        await checkpoints.SaveAsync(Processor, GrowingHeadBackend.FirstHead, ct);

        await waiting;

        backend.HeadReads.Should().Be(1,
            because: "the head is a snapshot taken when the caller asked, not a moving target");
    }

    [Fact]
    public async Task WaitForProjection_ObservesCancellationRatherThanRunningOutTheTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var checkpoints = new InMemoryCheckpointStore();
        var catchUp = new ProjectionCatchUp(new InMemoryEventStoreBackend(), checkpoints);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waiting = catchUp.WaitForProjectionAsync(
            Processor, position: 1, TimeSpan.FromSeconds(30), cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    /// <summary>
    /// A backend whose head has moved on by the time you ask again. Hand-written rather than
    /// driven by concurrent appends to the in-memory backend, because the point of the test is
    /// which head the wait targets, and that must not hinge on which task the scheduler ran first.
    /// </summary>
    private sealed class GrowingHeadBackend : IEventStoreBackend
    {
        public const long FirstHead = 100;

        private long _reads;

        public int HeadReads => (int)Interlocked.Read(ref _reads);

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Increment(ref _reads) * FirstHead);

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query, long afterPosition = 0, int? limit = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0, int? limit = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery = null,
            long? expectedPosition = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
