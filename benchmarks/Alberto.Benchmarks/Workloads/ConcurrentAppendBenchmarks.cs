using Alberto;
using Alberto.Benchmarks.Harness;
using Alberto.Postgres;
using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Alberto.Benchmarks.Workloads;

/// <summary>
/// What each concurrent append case's own tag boundary is, and therefore whether it can
/// conflict with a sibling writer.
/// </summary>
public enum AppendBoundary
{
    /// <summary>Each writer owns a tag no other writer touches — never conflicts.</summary>
    Disjoint,

    /// <summary>Every writer targets the same tag — conflicts are the point.</summary>
    Shared,

    /// <summary>No DCB query at all — isolates the append lock and insert cost from the
    /// conflict-check scan that <see cref="Disjoint"/>/<see cref="Shared"/> also pay.</summary>
    NoCondition,
}

/// <summary>
/// Does the single per-store advisory append lock
/// (<c>PostgresBackendHelpers.AcquireAppendLockAsync</c>, keyed
/// <c>alberto-append:{schema}</c> for the single-tenant backend this class uses — one key for
/// the *whole store*, not per boundary) cap throughput even when writers' consistency
/// boundaries never overlap?
///
/// <see cref="AppendBoundary.Disjoint"/> is the case that matters: every writer's tag is
/// unique, so nothing in the DCB semantics requires serializing them, but today's lock key
/// does not know that — it locks the whole store regardless of which tags a writer names.
/// If <c>Disjoint</c> throughput does not scale with <c>Writers</c>, that is the lock, not
/// genuine contention, and it is what a per-boundary locking redesign would target.
/// <see cref="AppendBoundary.Shared"/> is the control that should NOT scale — real conflicts
/// exist there regardless of locking strategy — and <see cref="AppendBoundary.NoCondition"/>
/// isolates the lock+insert cost from the conflict-check SELECT the other two also pay.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class ConcurrentAppendBenchmarks
{
    // Total appends attempted per invocation, split evenly across Writers. Fixed rather than
    // Writers-scaled so OperationsPerInvoke — which BenchmarkDotNet requires as a compile-time
    // constant — can still report a genuine per-append mean time at every Writers value: the
    // same amount of work is always divided by the same OperationsPerInvoke, just across more
    // or fewer concurrent tasks. 256 is divisible by every value in Writers below.
    private const int TotalAppends = 256;

    private NpgsqlDataSource _dataSource = null!;
    private PostgresEventStoreBackend _backend = null!;
    private long _seededHead;
    private long _successes;
    private long _conflicts;

    [Params(1, 4, 16, 32)]
    public int Writers { get; set; }

    [Params(AppendBoundary.Disjoint, AppendBoundary.Shared, AppendBoundary.NoCondition)]
    public AppendBoundary Boundary { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var database = await BenchmarkDatabase.Instance;
        var connectionString = await database.CloneAsync(
            StoreSizes.Medium, $"{nameof(ConcurrentAppendBenchmarks)}{Writers}{Boundary}");

        // BenchmarkDatabase.CloneAsync caps MaxPoolSize at 10 for the rest of the suite, which
        // is fine for the single-connection cases elsewhere but would make the pool itself the
        // bottleneck here once Writers exceeds it. Widen it back out, above Writers, so what
        // gets measured is the append lock and not Npgsql's pool queue.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = Math.Max(Writers * 2, 20) };
        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        _backend = new PostgresEventStoreBackend(_dataSource);
        _seededHead = await _backend.GetLastPositionAsync();

        // Light touch only: this path is lock/round-trip dominated, not plan-shape dominated
        // the way the read family is (see Warmup.cs's 2000x finding) — a single real round
        // through every writer's path is enough to settle connections and plans.
        await RunRoundAsync();
        ResetToSeededHead();
        _successes = 0;
        _conflicts = 0;
    }

    /// <summary>Same reasoning as AppendBenchmarkBase: keep every iteration's store the same size.</summary>
    [IterationCleanup]
    public void ResetToSeededHead()
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alberto_events WHERE global_position > @head";
        command.Parameters.AddWithValue("head", _seededHead);
        command.ExecuteNonQuery();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (Boundary == AppendBoundary.Shared)
        {
            // BenchmarkDotNet has no first-class channel for a second metric alongside Mean,
            // and the conflict rate is the whole point of the Shared case, so it goes to the
            // console instead — grep the run's stdout for "[conflict-rate]".
            //
            // attempts covers every RunConcurrentAppends call GlobalCleanup sees: BenchmarkDotNet's
            // own warm-up iterations plus the measured ones (the GlobalSetup warm-up round is
            // excluded — counters are zeroed after it), so it is not the same as --iterationCount.
            // The rate itself doesn't care: it's a ratio over however many rounds ran.
            var attempts = _successes + _conflicts;
            var rate = attempts == 0 ? 0 : (double)_conflicts / attempts;
            Console.WriteLine(
                $"[conflict-rate] Writers={Writers} attempts={attempts} (BDN warm-up + measured) " +
                $"successes={_successes} conflicts={_conflicts} rate={rate:P1}");
        }

        await _dataSource.DisposeAsync();
    }

    [Benchmark(OperationsPerInvoke = TotalAppends), BenchmarkCategory(Categories.Append, Categories.Smoke)]
    public Task RunConcurrentAppends() => RunRoundAsync();

    private Task RunRoundAsync()
    {
        var perWriter = TotalAppends / Writers;
        var tasks = new Task[Writers];
        for (var w = 0; w < Writers; w++)
        {
            var writerIndex = w;
            tasks[w] = Task.Run(() => RunWriterAsync(writerIndex, perWriter));
        }

        return Task.WhenAll(tasks);
    }

    private async Task RunWriterAsync(int writerIndex, int count)
    {
        var tag = Boundary == AppendBoundary.Shared
            ? new EventTag("shared", "conflict")
            : new EventTag("writer", writerIndex.ToString());
        var query = Boundary == AppendBoundary.NoCondition ? null : DcbQuery.ByTags(tag);

        for (var i = 0; i < count; i++)
        {
            long? expectedPosition = null;
            if (query is not null)
            {
                // "Read position for the boundary, then append with condition after that
                // position" — the same read-then-append cycle a command handler runs. Bounded
                // at TotalAppends: that is the most this tag can ever hold within one
                // invocation (Shared accumulates across every writer; Disjoint only from this
                // one), so it always sees the whole boundary.
                var current = await _backend.StreamAsync(query, afterPosition: 0, limit: TotalAppends);
                expectedPosition = current.Count > 0 ? current.Max(e => e.GlobalPosition) : 0;
            }

            // Fresh Id per append — EventToPersist.Id defaults via Guid.CreateVersion7() at
            // construction, so reusing one instance across iterations reappends the same id
            // and trips the alberto_events_event_id_key unique constraint on the second write.
            var eventToAppend = new EventToPersist
            {
                EventType = new EventType("writer-op"),
                Tags = [tag],
                EventData = """{"op":true}""",
            };

            try
            {
                await _backend.AppendAsync([eventToAppend], dcbQuery: query, expectedPosition: expectedPosition);
                if (Boundary == AppendBoundary.Shared)
                    Interlocked.Increment(ref _successes);
            }
            catch (DcbConflictException) when (Boundary == AppendBoundary.Shared)
            {
                // No retry: the point of this case is the conflict rate itself, so retrying
                // would hide the thing being measured rather than recovering a throughput
                // number nobody asked for here.
                Interlocked.Increment(ref _conflicts);
            }
        }
    }
}

/// <summary>
/// The multi-tenant control: does giving disjoint writers different tenants — and so
/// different advisory lock keys (<c>alberto-append:{schema}:{tenantId}</c>, see
/// <see cref="PostgresTenantEventStoreBackend"/>) — actually buy throughput, as the natural
/// control against <see cref="ConcurrentAppendBenchmarks"/>'s single-tenant
/// <see cref="AppendBoundary.Disjoint"/> case (same schema-wide lock for every writer)?
///
/// Cheapest version, deliberately: going through <c>.WithTenancy()</c> would mean a full
/// module/DI setup this suite has no other use for, so this constructs
/// <see cref="PostgresTenantEventStoreBackend"/> directly against a freshly migrated
/// multi-tenant database (empty — these writers create their own small store, so the
/// template-clone/seed machinery StoreSizes exists for would be pure overhead here).
/// No DCB query on either side (mirrors <see cref="AppendBoundary.NoCondition"/>), so what's
/// isolated is exactly the lock, not the conflict-check scan the single-tenant class also prices.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class TenantConcurrentAppendBenchmarks
{
    private const int TotalAppends = 256;

    private NpgsqlDataSource _dataSource = null!;
    private PostgresTenantEventStoreBackend _backend = null!;

    [Params(1, 4, 16, 32)]
    public int Writers { get; set; }

    [Params(true, false)]
    public bool DifferentTenants { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var database = await BenchmarkDatabase.Instance;
        var connectionString = await database.CreateFreshDatabaseAsync(
            $"{nameof(TenantConcurrentAppendBenchmarks)}{Writers}{DifferentTenants}", singleTenant: false);

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = Math.Max(Writers * 2, 20) };
        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        _backend = new PostgresTenantEventStoreBackend(_dataSource);

        await RunRoundAsync();
        ResetToEmpty();
    }

    [IterationCleanup]
    public void ResetToEmpty()
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alberto_events";
        command.ExecuteNonQuery();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _dataSource.DisposeAsync();

    [Benchmark(OperationsPerInvoke = TotalAppends), BenchmarkCategory(Categories.Append, Categories.Smoke)]
    public Task RunConcurrentAppends() => RunRoundAsync();

    private Task RunRoundAsync()
    {
        var perWriter = TotalAppends / Writers;
        var tasks = new Task[Writers];
        for (var w = 0; w < Writers; w++)
        {
            var writerIndex = w;
            tasks[w] = Task.Run(() => RunWriterAsync(writerIndex, perWriter));
        }

        return Task.WhenAll(tasks);
    }

    private async Task RunWriterAsync(int writerIndex, int count)
    {
        // The variable under test: same tenant id for every writer (shares the lock key) vs
        // one tenant per writer (each gets its own).
        var tenantId = DifferentTenants ? $"tenant-{writerIndex}" : "tenant-shared";

        for (var i = 0; i < count; i++)
        {
            // Fresh Id per append — see the comment in ConcurrentAppendBenchmarks.RunWriterAsync.
            var eventToAppend = new EventToPersist
            {
                EventType = new EventType("writer-op"),
                Tags = [new EventTag("writer", writerIndex.ToString())],
                EventData = """{"op":true}""",
            };
            await _backend.AppendForTenant(tenantId, [eventToAppend]);
        }
    }
}
