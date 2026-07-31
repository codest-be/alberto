using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

public sealed class DeadLetterRetryLoopTests
{
    [Fact]
    public async Task DisposeAsync_AfterStart_DoesNotPropagateCancellation()
    {
        var loop = new DeadLetterRetryLoop(
            new TestProcessor(),
            new TestDeadLetterStore(),
            pollingInterval: TimeSpan.FromMinutes(5));

        await loop.StartAsync(TestContext.Current.CancellationToken);

        await loop.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_AfterDisposeAsync_IsIgnored()
    {
        var loop = new DeadLetterRetryLoop(
            new TestProcessor(),
            new TestDeadLetterStore(),
            pollingInterval: TimeSpan.FromMinutes(5));

        await loop.StartAsync(TestContext.Current.CancellationToken);
        await loop.DisposeAsync();
        await loop.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class TestProcessor : IEventProcessor
    {
        public string ProcessorId => "test-processor";

        public bool IsActive { get; set; } = true;

        public bool IsRebuilding { get; set; }

        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestDeadLetterStore : IClaimableDeadLetterStore
    {
        public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
            string processorId,
            string? tenantId = null,
            int limit = 100,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);

        public Task<int> CountAsync(string processorId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task ClearAsync(string processorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkForRetryAsync(string processorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
            string processorId,
            int batchSize,
            TimeSpan leaseDuration,
            string claimedBy,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterClaim>>([]);

        public Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
