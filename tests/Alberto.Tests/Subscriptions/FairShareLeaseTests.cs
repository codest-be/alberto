using Alberto.InMemory;
using Alberto.Leases;
using Alberto.Postgres;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// <see cref="Configuration.ProcessorLeaseOptions.Rebalance"/>: a replica holds at most
/// ceil(processors / live replicas), sheds the rest by leaving them out of its renewal, and a
/// replica that joins late picks up what was shed once those leases expire.
/// </summary>
public sealed class FairShareLeaseTests
{
    private const string Consumer = "fair-share";
    private static readonly string[] Processors = ["p0", "p1", "p2", "p3", "p4", "p5"];

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    [Fact]
    public async Task LateReplica_GetsHalf_AfterTheFirstSheds_AndTheSurvivorTakesAllBackWhenItDies()
    {
        var leases = new InMemoryProcessorLeaseManager(_clock, TimeSpan.FromSeconds(30));

        await using var a = Group(leases, "a");
        await a.StartAsync(CancellationToken.None);
        Holders(leases).Should().Equal(Enumerable.Repeat("a", 6));

        // B is present but finds every processor held.
        await using var b = Group(leases, "b");
        await b.StartAsync(CancellationToken.None);
        Holders(leases).Should().Equal(Enumerable.Repeat("a", 6));
        (await leases.CountLiveReplicasAsync(Consumer, CancellationToken.None)).Should().Be(2);

        // A's renewal keeps its share and leaves the rest to expire.
        _clock.Advance(TimeSpan.FromSeconds(20));
        await a.RenewOnceAsync();
        await b.RenewOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(11));
        Holders(leases).Should().Equal("a", "a", "a", null, null, null);

        // A scan does not take back what the renewal shed.
        await a.ScanOnceAsync();
        await b.ScanOnceAsync();
        Holders(leases).Should().Equal("a", "a", "a", "b", "b", "b");

        // B stops renewing (crashed). Once its leases and its presence expire, A's share is all six.
        _clock.Advance(TimeSpan.FromSeconds(20));
        await a.RenewOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(11));
        await a.ScanOnceAsync();
        Holders(leases).Should().Equal(Enumerable.Repeat("a", 6));
    }

    [Fact]
    public async Task Shedding_FlushesCheckpointsWhileTheShedLeasesFenceTokenIsStillOnRecord()
    {
        var leases = new InMemoryProcessorLeaseManager(_clock, TimeSpan.FromSeconds(30));
        long tokenAtFlush = -1;
        LeaseAwareControlLoopGroup? a = null;
        a = Group(leases, "a", _ => { tokenAtFlush = a!.GetFenceToken("p5"); return Task.CompletedTask; });
        await using var _ = a;
        await a.StartAsync(CancellationToken.None);

        // Another replica's presence halves A's share.
        await leases.TryAcquireAsync(Consumer, "alberto:replica:b", "b");
        await a.RenewOnceAsync();

        tokenAtFlush.Should().BeGreaterThan(0);
        a.GetFenceToken("p5").Should().Be(0);
        a.GetFenceToken("p0").Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AcquiringALease_ForgetsTheCachedCheckpoint_BeforeTheLoopStarts()
    {
        var leases = new InMemoryProcessorLeaseManager(_clock, TimeSpan.FromSeconds(30));
        var forgotten = new List<string>();
        await using var a = new LeaseAwareControlLoopGroup(
            Processors.Select(BuildLoop).ToList(), leases, Consumer, "a", rebalance: true,
            forgetCheckpoint: (p, _) => { forgotten.Add(p); return Task.CompletedTask; });

        await a.StartAsync(CancellationToken.None);

        forgotten.Should().Equal(Processors);
    }

    [Fact]
    public void Rebalance_WithALeaseManagerThatCannotRenewASubset_Throws()
    {
        var act = () => new LeaseAwareControlLoopGroup(
            [], new RenewAllOnly(), Consumer, "a", rebalance: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Rebalance*");
    }

    private string?[] Holders(InMemoryProcessorLeaseManager leases) =>
        Processors.Select(p => leases.GetActiveLease(Consumer, p)?.ReplicaId).ToArray();

    internal static LeaseAwareControlLoopGroup Group(
        IProcessorLeaseManager leases, string replicaId, Func<CancellationToken, Task>? flush = null) =>
        new(Processors.Select(BuildLoop).ToList(), leases, Consumer, replicaId,
            flushCheckpoints: flush, rebalance: true);

    internal static LeaseAwareControlLoopGroup Group(IProcessorLeaseManager leases, string consumer, string replicaId) =>
        new(Processors.Select(BuildLoop).ToList(), leases, consumer, replicaId, rebalance: true);

    private static ControlLoop BuildLoop(string processorId)
    {
        var backend = new InMemoryEventStoreBackend();
        return new ControlLoop(new StubProcessor(processorId), new EventStoreHead(backend, TimeSpan.FromMilliseconds(50)),
            backend, new InMemoryCheckpointStore(),
            pollingInterval: TimeSpan.FromMilliseconds(50), batchSize: 1,
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled },
            drainTimeout: TimeSpan.FromMilliseconds(200));
    }

    private sealed class StubProcessor(string processorId) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>();

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RenewAllOnly : IProcessorLeaseManager
    {
        public TimeSpan LeaseDuration => TimeSpan.FromHours(1);

        public Task<IProcessorLease?> TryAcquireAsync(string c, string p, string r, CancellationToken ct = default) =>
            Task.FromResult<IProcessorLease?>(null);

        public Task<IReadOnlyList<string>> RenewLeasesAsync(string c, string r, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task ReleaseAllLeasesAsync(string c, string r, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>The same story on Postgres, with real one-second leases and real timers.</summary>
[Trait("Category", "Integration")]
public sealed class FairShareLeasePostgresTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    private readonly string _consumer = $"fair-share-{Guid.NewGuid():N}";

    [Fact]
    public async Task TwoReplicas_SplitSixProcessors_AndTheSurvivorTakesAllWhenOneLeaves()
    {
        var ct = TestContext.Current.CancellationToken;
        var leases = new PostgresProcessorLeaseManager(fixture.DataSource, leaseDuration: TimeSpan.FromSeconds(1));

        await using var a = FairShareLeaseTests.Group(leases, _consumer, "a");
        await a.StartAsync(ct);
        (await HeldAsync(ct)).Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 6 });

        var b = FairShareLeaseTests.Group(leases, _consumer, "b");
        try
        {
            await b.StartAsync(ct);
            await UntilAsync(h => h.GetValueOrDefault("a") == 3 && h.GetValueOrDefault("b") == 3, ct);
        }
        finally
        {
            await b.DisposeAsync();
        }

        await UntilAsync(h => h.GetValueOrDefault("a") == 6, ct);
    }

    private async Task UntilAsync(Func<Dictionary<string, long>, bool> done, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        Dictionary<string, long> held;
        while (!done(held = await HeldAsync(ct)) && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);
        done(held).Should().BeTrue($"held was {string.Join(", ", held.Select(h => $"{h.Key}={h.Value}"))}");
    }

    private async Task<Dictionary<string, long>> HeldAsync(CancellationToken ct)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT replica_id, count(*) FROM alberto_processor_leases " +
            "WHERE consumer_id = @consumer AND processor_id NOT LIKE 'alberto:replica:%' AND expires_at > now() " +
            "GROUP BY replica_id");
        command.Parameters.AddWithValue("consumer", _consumer);
        var held = new Dictionary<string, long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            held[reader.GetString(0)] = reader.GetInt64(1);
        return held;
    }
}
