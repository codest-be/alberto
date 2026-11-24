using Alberto.InMemory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Direct unit tests for <see cref="InMemoryProcessorLeaseManager"/>, covering
/// acquire/renew/release/list behaviour that the shared
/// <see cref="FencedCheckpointStoreSpecification"/> conformance suite does not reach
/// (it only exercises <c>GetActiveLease</c> and <c>TryAcquireAsync</c> indirectly through
/// <see cref="InMemoryFencedCheckpointStore"/>). All time control goes through
/// <see cref="FakeTimeProvider"/> — no Docker, no wall-clock waits.
/// </summary>
public sealed class InMemoryProcessorLeaseManagerTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly InMemoryProcessorLeaseManager _manager;

    public InMemoryProcessorLeaseManagerTests()
    {
        _manager = new InMemoryProcessorLeaseManager(_clock, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void LeaseDuration_reflects_the_constructor_argument()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), _manager.LeaseDuration);
    }

    [Fact]
    public void LeaseDuration_defaults_to_30_seconds_when_not_specified()
    {
        var defaulted = new InMemoryProcessorLeaseManager();
        Assert.Equal(TimeSpan.FromSeconds(30), defaulted.LeaseDuration);
    }

    [Fact]
    public async Task TryAcquireAsync_WithNoExistingLease_Succeeds()
    {
        var lease = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        Assert.NotNull(lease);
        Assert.Equal("proc", lease!.ProcessorId);
        Assert.Equal(1, lease.FenceToken);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromSeconds(30), lease.ExpiresAt);
    }

    [Fact]
    public async Task TryAcquireAsync_IssuesStrictlyIncreasingFenceTokensAcrossDifferentProcessors()
    {
        var first = await _manager.TryAcquireAsync("consumer", "proc-a", "replica-1");
        var second = await _manager.TryAcquireAsync("consumer", "proc-b", "replica-1");

        Assert.True(second!.FenceToken > first!.FenceToken);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenHeldByAnotherReplicaAndNotExpired_ReturnsNull()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        var contended = await _manager.TryAcquireAsync("consumer", "proc", "replica-2");

        Assert.Null(contended);
    }

    [Fact]
    public async Task TryAcquireAsync_BySameReplica_IssuesNewTokenAndExtendsExpiry()
    {
        var first = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(5));
        var second = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        Assert.NotNull(second);
        Assert.True(second!.FenceToken > first!.FenceToken);

        // the expiry must extend from the re-acquisition time
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromSeconds(30), _manager.GetActiveLease("consumer", "proc")!.Value.ExpiresAt);
    }

    [Fact]
    public async Task TryAcquireAsync_AfterExpiry_AnotherReplicaCanClaim()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(31));
        var stolen = await _manager.TryAcquireAsync("consumer", "proc", "replica-2");

        Assert.NotNull(stolen);
        // replica-2 now holds the lease
        Assert.Null((await _manager.TryAcquireAsync("consumer", "proc", "replica-1")));
    }

    [Fact]
    public async Task TryAcquireAsync_ExactlyAtExpiry_IsTreatedAsExpired()
    {
        // ExpiresAt <= now is the expiry test in TryAcquireAsync: the boundary itself must
        // read as expired (a mutant flipping <= to < would let replica-1 keep the lease here).
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(30));
        var claimed = await _manager.TryAcquireAsync("consumer", "proc", "replica-2");

        // a lease that expires exactly now must be claimable by another replica
        Assert.NotNull(claimed);
    }

    [Fact]
    public async Task TryAcquireAsync_OneTickBeforeExpiry_StillBlocksAnotherReplica()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        var contended = await _manager.TryAcquireAsync("consumer", "proc", "replica-2");

        // the lease has not yet expired
        Assert.Null(contended);
    }

    [Fact]
    public async Task RenewLeasesAsync_ExtendsOnlyLiveLeasesOwnedByTheReplica()
    {
        await _manager.TryAcquireAsync("consumer", "mine", "replica-1");
        await _manager.TryAcquireAsync("consumer", "theirs", "replica-2");

        _clock.Advance(TimeSpan.FromSeconds(10));
        var renewed = await _manager.RenewLeasesAsync("consumer", "replica-1");

        Assert.Equivalent(new[] { "mine" }, renewed, strict: true);

        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromSeconds(30), _manager.GetActiveLease("consumer", "mine")!.Value.ExpiresAt);
        Assert.NotEqual(_clock.GetUtcNow() + TimeSpan.FromSeconds(30), _manager.GetActiveLease("consumer", "theirs")!.Value.ExpiresAt);
    }

    [Fact]
    public async Task RenewLeasesAsync_DoesNotRenewLeasesFromAnotherConsumer()
    {
        await _manager.TryAcquireAsync("consumer-a", "proc", "replica-1");
        await _manager.TryAcquireAsync("consumer-b", "other", "replica-1");

        var renewed = await _manager.RenewLeasesAsync("consumer-a", "replica-1");

        Assert.Equivalent(new[] { "proc" }, renewed, strict: true);
    }

    [Fact]
    public async Task RenewLeasesAsync_DoesNotRenewAnExpiredLease()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");
        _clock.Advance(TimeSpan.FromSeconds(31));

        var renewed = await _manager.RenewLeasesAsync("consumer", "replica-1");

        Assert.Empty(renewed);
    }

    [Fact]
    public async Task RenewLeasesAsync_WithNoLeases_ReturnsEmpty()
    {
        var renewed = await _manager.RenewLeasesAsync("consumer", "replica-1");

        Assert.Empty(renewed);
    }

    [Fact]
    public async Task ReleaseAllLeasesAsync_RemovesOnlyLeasesOwnedByThatReplicaInThatConsumer()
    {
        await _manager.TryAcquireAsync("consumer", "mine", "replica-1");
        await _manager.TryAcquireAsync("consumer", "theirs", "replica-2");
        await _manager.TryAcquireAsync("other-consumer", "mine-elsewhere", "replica-1");

        await _manager.ReleaseAllLeasesAsync("consumer", "replica-1");

        Assert.Null(_manager.GetActiveLease("consumer", "mine"));
        Assert.NotNull(_manager.GetActiveLease("consumer", "theirs"));
        // releasing one consumer's leases must not touch another consumer's
        Assert.NotNull(_manager.GetActiveLease("other-consumer", "mine-elsewhere"));
    }

    [Fact]
    public void LeaseDuration_honours_a_non_default_argument()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), new InMemoryProcessorLeaseManager(_clock, TimeSpan.FromSeconds(7)).LeaseDuration);
    }

    [Fact]
    public async Task GetActiveLease_ExactlyAtExpiry_ReturnsNull()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.NotNull(_manager.GetActiveLease("consumer", "proc"));

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(_manager.GetActiveLease("consumer", "proc"));
    }

    [Fact]
    public async Task TryAcquireAsync_BySameReplicaAfterExpiry_IssuesNewToken()
    {
        var first = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(31));
        var second = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        Assert.True(second!.FenceToken > first!.FenceToken);
    }

    [Fact]
    public async Task RenewLeasesAsync_ExactlyAtExpiry_DoesNotRenew()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Empty((await _manager.RenewLeasesAsync("consumer", "replica-1")));
    }
}
