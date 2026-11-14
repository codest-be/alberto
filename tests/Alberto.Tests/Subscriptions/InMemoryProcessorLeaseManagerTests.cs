using Alberto.InMemory;
using FluentAssertions;
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
        _manager.LeaseDuration.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void LeaseDuration_defaults_to_30_seconds_when_not_specified()
    {
        var defaulted = new InMemoryProcessorLeaseManager();
        defaulted.LeaseDuration.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TryAcquireAsync_WithNoExistingLease_Succeeds()
    {
        var lease = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        lease.Should().NotBeNull();
        lease!.ProcessorId.Should().Be("proc");
        lease.FenceToken.Should().Be(1);
        lease.ExpiresAt.Should().Be(_clock.GetUtcNow() + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TryAcquireAsync_IssuesStrictlyIncreasingFenceTokensAcrossDifferentProcessors()
    {
        var first = await _manager.TryAcquireAsync("consumer", "proc-a", "replica-1");
        var second = await _manager.TryAcquireAsync("consumer", "proc-b", "replica-1");

        second!.FenceToken.Should().BeGreaterThan(first!.FenceToken);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenHeldByAnotherReplicaAndNotExpired_ReturnsNull()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        var contended = await _manager.TryAcquireAsync("consumer", "proc", "replica-2");

        contended.Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_BySameReplica_IssuesNewTokenAndExtendsExpiry()
    {
        var first = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(5));
        var second = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        second.Should().NotBeNull();
        second!.FenceToken.Should().BeGreaterThan(first!.FenceToken);

        _manager.GetActiveLease("consumer", "proc")!.Value.ExpiresAt
            .Should().Be(_clock.GetUtcNow() + TimeSpan.FromSeconds(30), "the expiry must extend from the re-acquisition time");
    }

    [Fact]
    public async Task TryAcquireAsync_AfterExpiry_AnotherReplicaCanClaim()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(31));
        var stolen = await _manager.TryAcquireAsync("consumer", "proc", "replica-2");

        stolen.Should().NotBeNull();
        (await _manager.TryAcquireAsync("consumer", "proc", "replica-1")).Should().BeNull("replica-2 now holds the lease");
    }

    [Fact]
    public async Task TryAcquireAsync_ExactlyAtExpiry_IsTreatedAsExpired()
    {
        // ExpiresAt <= now is the expiry test in TryAcquireAsync: the boundary itself must
        // read as expired (a mutant flipping <= to < would let replica-1 keep the lease here).
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(30));
        var claimed = await _manager.TryAcquireAsync("consumer", "proc", "replica-2");

        claimed.Should().NotBeNull("a lease that expires exactly now must be claimable by another replica");
    }

    [Fact]
    public async Task TryAcquireAsync_OneTickBeforeExpiry_StillBlocksAnotherReplica()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        var contended = await _manager.TryAcquireAsync("consumer", "proc", "replica-2");

        contended.Should().BeNull("the lease has not yet expired");
    }

    [Fact]
    public async Task RenewLeasesAsync_ExtendsOnlyLiveLeasesOwnedByTheReplica()
    {
        await _manager.TryAcquireAsync("consumer", "mine", "replica-1");
        await _manager.TryAcquireAsync("consumer", "theirs", "replica-2");

        _clock.Advance(TimeSpan.FromSeconds(10));
        var renewed = await _manager.RenewLeasesAsync("consumer", "replica-1");

        renewed.Should().BeEquivalentTo(["mine"]);

        _manager.GetActiveLease("consumer", "mine")!.Value.ExpiresAt.Should().Be(_clock.GetUtcNow() + TimeSpan.FromSeconds(30));
        _manager.GetActiveLease("consumer", "theirs")!.Value.ExpiresAt.Should().NotBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RenewLeasesAsync_DoesNotRenewLeasesFromAnotherConsumer()
    {
        await _manager.TryAcquireAsync("consumer-a", "proc", "replica-1");
        await _manager.TryAcquireAsync("consumer-b", "other", "replica-1");

        var renewed = await _manager.RenewLeasesAsync("consumer-a", "replica-1");

        renewed.Should().BeEquivalentTo(["proc"]);
    }

    [Fact]
    public async Task RenewLeasesAsync_DoesNotRenewAnExpiredLease()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");
        _clock.Advance(TimeSpan.FromSeconds(31));

        var renewed = await _manager.RenewLeasesAsync("consumer", "replica-1");

        renewed.Should().BeEmpty();
    }

    [Fact]
    public async Task RenewLeasesAsync_WithNoLeases_ReturnsEmpty()
    {
        var renewed = await _manager.RenewLeasesAsync("consumer", "replica-1");

        renewed.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAllLeasesAsync_RemovesOnlyLeasesOwnedByThatReplicaInThatConsumer()
    {
        await _manager.TryAcquireAsync("consumer", "mine", "replica-1");
        await _manager.TryAcquireAsync("consumer", "theirs", "replica-2");
        await _manager.TryAcquireAsync("other-consumer", "mine-elsewhere", "replica-1");

        await _manager.ReleaseAllLeasesAsync("consumer", "replica-1");

        _manager.GetActiveLease("consumer", "mine").Should().BeNull();
        _manager.GetActiveLease("consumer", "theirs").Should().NotBeNull();
        _manager.GetActiveLease("other-consumer", "mine-elsewhere").Should().NotBeNull("releasing one consumer's leases must not touch another consumer's");
    }

    [Fact]
    public void LeaseDuration_honours_a_non_default_argument()
    {
        new InMemoryProcessorLeaseManager(_clock, TimeSpan.FromSeconds(7)).LeaseDuration
            .Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task GetActiveLease_ExactlyAtExpiry_ReturnsNull()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        _manager.GetActiveLease("consumer", "proc").Should().NotBeNull();

        _clock.Advance(TimeSpan.FromTicks(1));
        _manager.GetActiveLease("consumer", "proc").Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_BySameReplicaAfterExpiry_IssuesNewToken()
    {
        var first = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        _clock.Advance(TimeSpan.FromSeconds(31));
        var second = await _manager.TryAcquireAsync("consumer", "proc", "replica-1");

        second!.FenceToken.Should().BeGreaterThan(first!.FenceToken);
    }

    [Fact]
    public async Task RenewLeasesAsync_ExactlyAtExpiry_DoesNotRenew()
    {
        await _manager.TryAcquireAsync("consumer", "proc", "replica-1");
        _clock.Advance(TimeSpan.FromSeconds(30));

        (await _manager.RenewLeasesAsync("consumer", "replica-1")).Should().BeEmpty();
    }
}
