using Alberto.Checkpoints;
using Alberto.Leases;
using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>A private database, so the fence-token sequence moves only for these tests.</summary>
public sealed class BatchedCheckpointFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

/// <summary>
/// The set-based checkpoint and lease statements: a resync is one read, a flush one write, a
/// leased save one fenced write that also detects an operator reset or rewind, and a lease scan
/// one acquire that draws a fence token only for the leases it wins.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BatchedCheckpointAndLeaseTests(BatchedCheckpointFixture fixture)
    : IClassFixture<BatchedCheckpointFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PostgresCheckpointStore Store() => new(fixture.DataSource);

    private ICheckpointBatchStore Batch() => Store();

    private PostgresProcessorLeaseManager Leases() => new(fixture.DataSource);

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task GetMany_ReturnsEachStoredCheckpoint_AndNullForAMissingOne()
    {
        var (a, b, missing) = (NewId("a"), NewId("b"), NewId("missing"));
        await Store().SaveAsync(a, 10, Ct);
        await Store().SaveAsync(b, 20, Ct);

        var stored = await Batch().GetManyAsync([a, b, missing], Ct);

        Assert.Equivalent(new Dictionary<string, long?> { [a] = 10, [b] = 20, [missing] = null }, stored, strict: true);
    }

    [Fact]
    public async Task SaveMany_Unfenced_InsertsAndAdvancesMonotonically()
    {
        var (existing, fresh, behind) = (NewId("existing"), NewId("fresh"), NewId("behind"));
        await Store().SaveAsync(existing, 10, Ct);
        await Store().SaveAsync(behind, 50, Ct);

        var outcomes = await Batch().SaveManyAsync(
            [new(existing, 30, 10, 0), new(fresh, 5, null, 0), new(behind, 20, 50, 0)], null, Ct);

        Assert.All(outcomes.Values, o => Assert.Equal(CheckpointWriteResult.Written, o.Result));
        Assert.Equal(30, await Store().GetAsync(existing, Ct));
        Assert.Equal(5, await Store().GetAsync(fresh, Ct));
        // SaveAsync stays monotonic
        Assert.Equal(50, await Store().GetAsync(behind, Ct));
    }

    [Fact]
    public async Task LeasedSave_HonoursAnOperatorRewind_AndReset()
    {
        var consumer = NewId("consumer");
        var (rewound, reset) = (NewId("rewound"), NewId("reset"));
        var leases = Leases();
        var tokens = new Dictionary<string, long>();
        foreach (var id in new[] { rewound, reset })
            tokens[id] = (await leases.TryAcquireAsync(consumer, id, "replica-a", Ct))!.FenceToken;

        await using var cache = new CachingCheckpointStore(Store(), TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        cache.SetFencingContext(new FencingContext(consumer, "replica-a", UseProcessorLeaseFencing: true, id => tokens[id]));
        var violations = 0;
        cache.OnFenceViolation = _ => violations++;

        await cache.SaveAsync(rewound, 100, Ct);
        await cache.SaveAsync(reset, 100, Ct);
        // a leased save writes through
        Assert.Equal(100, await Store().GetAsync(rewound, Ct));

        // The operator, through its own store.
        await Store().RewindAsync(rewound, 40, Ct);
        await Store().ResetAsync(reset, Ct);

        await cache.SaveAsync(rewound, 150, Ct);
        await cache.SaveAsync(reset, 150, Ct);

        // the write must not lift a rewind
        Assert.Equal(40, await Store().GetAsync(rewound, Ct));
        // the write must not recreate a reset row
        Assert.Null((await Store().GetAsync(reset, Ct)));
        Assert.Equal(40, await cache.GetAsync(rewound, Ct));
        Assert.Null((await cache.GetAsync(reset, Ct)));
        // an operator reset is not a lost lease
        Assert.Equal(0, violations);

        // From the lowered position the processor advances normally.
        await cache.SaveAsync(rewound, 60, Ct);
        Assert.Equal(60, await Store().GetAsync(rewound, Ct));
    }

    [Fact]
    public async Task SaveMany_WithAStaleFenceToken_IsFenced_AndWritesNothing()
    {
        var consumer = NewId("consumer");
        var processor = NewId("p");
        var leases = Leases();
        var stale = (await leases.TryAcquireAsync(consumer, processor, "replica-a", Ct))!.FenceToken;
        var fencing = new FencingContext(consumer, "replica-a", UseProcessorLeaseFencing: true);

        Assert.Equal(CheckpointWriteResult.Written, (await Batch().SaveManyAsync([new(processor, 10, null, stale)], fencing, Ct))[processor].Result);

        await ExpireLeaseAsync(consumer, processor);
        var current = (await leases.TryAcquireAsync(consumer, processor, "replica-a", Ct))!.FenceToken;
        Assert.True(current > stale);

        var outcome = await Batch().SaveManyAsync([new(processor, 99, 10, stale)], fencing, Ct);

        Assert.Equal(CheckpointWriteResult.Fenced, outcome[processor].Result);
        Assert.Equal(10, await Store().GetAsync(processor, Ct));
    }

    [Fact]
    public async Task AcquireMany_TwoReplicasContend_EachProcessorIsWonByExactlyOne()
    {
        var consumer = NewId("consumer");
        var ids = Enumerable.Range(0, 20).Select(i => $"p{i:00}").ToArray();
        var leases = Leases();

        // Half the rows exist already, so both the claim and the insert path contend.
        foreach (var id in ids.Take(10))
            await leases.TryAcquireAsync(consumer, id, "warmup", Ct);
        foreach (var id in ids.Take(10))
            await ExpireLeaseAsync(consumer, id);

        var results = await Task.WhenAll(
            Task.Run(() => leases.TryAcquireManyAsync(consumer, ids, "replica-a", Ct), Ct),
            Task.Run(() => leases.TryAcquireManyAsync(consumer, ids, "replica-b", Ct), Ct));

        var won = results.SelectMany(r => r.Select(l => l.ProcessorId)).ToList();
        Assert.Distinct(won);
        Assert.Equivalent(ids, won, strict: true);
        Assert.Distinct(results.SelectMany(r => r).Select(l => l.FenceToken));
    }

    [Fact]
    public async Task AcquireMany_LosingToALiveLease_DrawsNoFenceToken()
    {
        var consumer = NewId("consumer");
        var ids = new[] { NewId("p"), NewId("p"), NewId("p") };
        var leases = Leases();

        var first = await leases.TryAcquireManyAsync(consumer, ids, "replica-a", Ct);
        Assert.Equal(3, first.Count());

        var before = await FenceSequenceAsync();
        var lost = await leases.TryAcquireManyAsync(consumer, ids, "replica-b", Ct);
        var after = await FenceSequenceAsync();

        Assert.Empty(lost);
        // a losing acquire must not consume a fence token
        Assert.Equal(before, after);

        // Re-acquiring its own leases is a new generation for each.
        var again = await leases.TryAcquireManyAsync(consumer, ids, "replica-a", Ct);
        Assert.All(again.Select(l => l.FenceToken), t => Assert.True(t > before));
    }

    private async Task<long> FenceSequenceAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT last_value FROM alberto_fence_tokens", conn);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExpireLeaseAsync(string consumerId, string processorId)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE alberto_processor_leases
            SET expires_at = now() - INTERVAL '1 minute'
            WHERE consumer_id = @consumer_id AND processor_id = @processor_id
            """,
            conn);
        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("processor_id", processorId);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(Ct));
    }
}

/// <summary>The same batched write on the multi-tenant schema, in both fencing modes.</summary>
[Trait("Category", "Integration")]
public sealed class BatchedCheckpointMultiTenantTests(FenceTokenMultiTenantFixture fixture)
    : IClassFixture<FenceTokenMultiTenantFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveMany_IsFencedByTheProcessorLease_OrByATenantLease()
    {
        ICheckpointBatchStore batch = new PostgresCheckpointStore(fixture.DataSource);
        var leases = new PostgresProcessorLeaseManager(fixture.DataSource);
        var consumer = $"consumer-{Guid.NewGuid():N}";
        var (held, notHeld) = ($"held-{Guid.NewGuid():N}", $"not-held-{Guid.NewGuid():N}");
        var token = (await leases.TryAcquireAsync(consumer, held, "replica-a", Ct))!.FenceToken;

        var processorMode = await batch.SaveManyAsync(
            [new(held, 10, null, token), new(notHeld, 10, null, token)],
            new FencingContext(consumer, "replica-a", UseProcessorLeaseFencing: true), Ct);

        Assert.Equal(CheckpointWriteResult.Written, processorMode[held].Result);
        Assert.Equal(CheckpointWriteResult.Fenced, processorMode[notHeld].Result);

        var tenantMode = new FencingContext(consumer, "replica-a");
        // the replica holds no tenant lease
        Assert.Equal(CheckpointWriteResult.Fenced, (await batch.SaveManyAsync([new(held, 20, 10, 0)], tenantMode, Ct))[held].Result);

        await using (var conn = await fixture.DataSource.OpenConnectionAsync(Ct))
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_tenant_leases (consumer_id, tenant_id, replica_id, expires_at)
            VALUES (@consumer_id, 'tenant-1', 'replica-a', now() + INTERVAL '1 minute')
            """, conn))
        {
            cmd.Parameters.AddWithValue("consumer_id", consumer);
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal(CheckpointWriteResult.Written, (await batch.SaveManyAsync([new(held, 20, 10, 0)], tenantMode, Ct))[held].Result);
        Assert.Equal(20, await new PostgresCheckpointStore(fixture.DataSource).GetAsync(held, Ct));
    }
}
