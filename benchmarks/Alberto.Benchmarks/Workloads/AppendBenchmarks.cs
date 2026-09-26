using System.Diagnostics;
using Alberto;
using Alberto.Benchmarks.Core;
using Alberto.Benchmarks.Harness;
using Alberto.Postgres;
using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Alberto.Benchmarks.Workloads;

/// <summary>
/// Shared setup for the append workloads: a cloned store, a backend, and cleanup back to the
/// seeded head between iterations.
///
/// StoreSize is deliberately absent from every class below except
/// <see cref="AppendWithDcbCheckStoreSizeBenchmarks"/>: appending does not read, so table size
/// should not change the answer for the two cases that provably don't — but
/// <c>AppendWithDcbCheck</c> does read (the DCB conflict-check scan runs under the append
/// lock), and that assumption was never tested for it. See that class for the axis.
/// </summary>
public abstract class AppendBenchmarkBase
{
    protected NpgsqlDataSource DataSource = null!;
    protected PostgresEventStoreBackend Backend = null!;
    protected long SeededHead;

    /// <summary>
    /// Builds the store and warms <paramref name="measured"/> — and only it.
    ///
    /// Every concrete class calls this from one <c>[GlobalSetup(Target = ...)]</c> per benchmark
    /// method, passing the method that setup targets. See <see cref="Warmup"/> for why warming
    /// this class's *other* methods is actively harmful rather than merely wasteful.
    /// </summary>
    protected async Task InitAsync(Func<Task> measured, int storeSize = StoreSizes.Medium)
    {
        var database = await BenchmarkDatabase.Instance;
        var connectionString = await database.CloneAsync(storeSize, GetType().Name);

        DataSource = NpgsqlDataSource.Create(connectionString);
        Backend = new PostgresEventStoreBackend(DataSource);
        SeededHead = await Backend.GetLastPositionAsync();

        await OnSetupAsync();

        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < Warmup.Invocations && elapsed.Elapsed < Warmup.Budget; i++)
        {
            await measured();

            // Not optional. Every append case reuses pre-built events, which carry fixed ids,
            // so warming twice without resetting in between violates the event_id unique
            // constraint and fails the whole case.
            ResetToSeededHead();
        }

        ReclaimWarmUpChurn();
    }

    /// <summary>
    /// Undoes the physical damage the warm-up did, which is not the same thing as undoing its
    /// rows.
    ///
    /// Every warm-up cycle inserts and then deletes, so a 300-cycle warm-up leaves that many
    /// dead tuples behind in alberto_events and both position tables, with the index entries
    /// to match and planner statistics describing a table that no longer exists. Measurement
    /// then runs over the bloat, and eventually races an autovacuum that fires inside the
    /// timed region. It is not a small effect: without this the whole append family read
    /// ~30% slower than its own baseline, and AppendWithConflictDetected — whose check scans
    /// the order:1 tag index that the warm-up churns hardest — read 883us against 2300us.
    ///
    /// VACUUM cannot run inside a transaction, hence the bare command on its own connection.
    /// </summary>
    private void ReclaimWarmUpChurn()
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "VACUUM (ANALYZE) alberto_events, alberto_event_tag_positions, alberto_event_type_positions";
        command.CommandTimeout = 300;
        command.ExecuteNonQuery();
    }

    protected virtual Task OnSetupAsync() => Task.CompletedTask;

    /// <summary>
    /// Deletes everything this iteration appended, so each iteration starts against an
    /// identically sized table. Without it, iteration 50 measures a bigger store than
    /// iteration 1 and the run reports growth rather than the append path.
    /// </summary>
    [IterationCleanup]
    public void ResetToSeededHead()
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        // The tag and type position tables cascade from alberto_events.
        command.CommandText = "DELETE FROM alberto_events WHERE global_position > @head";
        command.Parameters.AddWithValue("head", SeededHead);
        command.ExecuteNonQuery();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DataSource.DisposeAsync();
}

/// <summary>Append cases with no parameter axis of their own.</summary>
[Config(typeof(BenchmarkConfig))]
public class AppendBenchmarks : AppendBenchmarkBase
{
    private IEventToPersist[] _single = null!;
    private DcbQuery _neverConflictingQuery = null!;
    private DcbQuery _alwaysConflictingQuery = null!;

    protected override Task OnSetupAsync()
    {
        // Pre-built so the measurement is the append path, not object construction.
        _single = [.. EventPlan.Build(1, seed: 7)];

        // Targets a tag never written, so the check always takes the no-conflict path
        // without throwing — the happy path worth measuring.
        _neverConflictingQuery = DcbQuery.ByTags(new EventTag("never", "used"));

        // Targets a tag the seed definitely wrote, so the check always finds a conflict.
        _alwaysConflictingQuery = DcbQuery.ByTags(new EventTag("order", "1"));

        return Task.CompletedTask;
    }

    [GlobalSetup(Target = nameof(SingleAppend))]
    public Task SetupSingleAppend() => InitAsync(SingleAppend);

    [GlobalSetup(Target = nameof(AppendWithDcbCheck))]
    public Task SetupAppendWithDcbCheck() => InitAsync(AppendWithDcbCheck);

    [GlobalSetup(Target = nameof(AppendWithConflictDetected))]
    public Task SetupAppendWithConflictDetected() => InitAsync(AppendWithConflictDetected);

    [Benchmark(Baseline = true), BenchmarkCategory(Categories.Append, Categories.Smoke)]
    public Task<IReadOnlyCollection<IEventEnvelope>> SingleAppend()
        => Backend.AppendAsync(_single);

    [Benchmark, BenchmarkCategory(Categories.Append)]
    public Task<IReadOnlyCollection<IEventEnvelope>> AppendWithDcbCheck()
        => Backend.AppendAsync(_single, dcbQuery: _neverConflictingQuery, expectedPosition: SeededHead);

    /// <summary>Prices the failure path — a conflict detected and thrown.</summary>
    [Benchmark, BenchmarkCategory(Categories.Append)]
    public async Task<bool> AppendWithConflictDetected()
    {
        try
        {
            await Backend.AppendAsync(_single, dcbQuery: _alwaysConflictingQuery, expectedPosition: 0);
            return false;
        }
        catch (DcbConflictException)
        {
            return true;
        }
    }
}

/// <summary>
/// Batch size sweep. Its own class because [Params] applies to every method in a class —
/// leaving BatchSize on AppendBenchmarks would run SingleAppend three times for nothing.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class BatchAppendBenchmarks : AppendBenchmarkBase
{
    private IEventToPersist[] _batch = null!;

    [Params(10, 100, 1000)]
    public int BatchSize { get; set; }

    protected override Task OnSetupAsync()
    {
        _batch = [.. EventPlan.Build(BatchSize, seed: 7)];
        return Task.CompletedTask;
    }

    [GlobalSetup(Target = nameof(BatchAppend))]
    public Task SetupBatchAppend() => InitAsync(BatchAppend);

    [Benchmark, BenchmarkCategory(Categories.Append)]
    public Task<IReadOnlyCollection<IEventEnvelope>> BatchAppend()
        => Backend.AppendAsync(_batch);
}

/// <summary>
/// Tag fan-out sweep — one event carrying many tags, which drives the tag-position table
/// write amplification. Separate class for the same [Params] reason as above.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class TagFanOutBenchmarks : AppendBenchmarkBase
{
    private IEventToPersist[] _fanOut = null!;

    [Params(1, 5, 20)]
    public int TagsPerEvent { get; set; }

    protected override Task OnSetupAsync()
    {
        _fanOut =
        [
            new EventToPersist
            {
                EventType = new EventType("order-placed"),
                Tags = [.. Enumerable.Range(1, TagsPerEvent)
                    .Select(i => new EventTag("order", $"fanout{i}"))],
                EventData = """{"orderId":"fanout","amount":9.99}""",
            },
        ];

        return Task.CompletedTask;
    }

    [GlobalSetup(Target = nameof(AppendWithTagFanOut))]
    public Task SetupAppendWithTagFanOut() => InitAsync(AppendWithTagFanOut);

    [Benchmark, BenchmarkCategory(Categories.Append)]
    public Task<IReadOnlyCollection<IEventEnvelope>> AppendWithTagFanOut()
        => Backend.AppendAsync(_fanOut);
}

/// <summary>
/// <c>AppendWithDcbCheck</c> at 10k/100k/1M — the one append case that reads (the DCB
/// conflict-check scan runs inside the append lock), so it is the one append case where
/// <c>docs/benchmarks/results.md</c>'s "appends carry no store-size axis" assumption is worth
/// actually testing. Own class so the axis doesn't triple <see cref="AppendBenchmarks"/>'s two
/// other cases, which never read at all.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class AppendWithDcbCheckStoreSizeBenchmarks : AppendBenchmarkBase
{
    private IEventToPersist[] _single = null!;
    private DcbQuery _realHistoryQuery = null!;

    [Params(StoreSizes.Small, StoreSizes.Medium, StoreSizes.Large)]
    public int StoreSize { get; set; }

    protected override Task OnSetupAsync()
    {
        _single = [.. EventPlan.Build(1, seed: 7)];

        // The append function's conflict check (alberto_append_events in
        // SingleTenant/004_StructuredConflictPosition.sql) is:
        //   SELECT global_position FROM alberto_event_tag_positions
        //   WHERE tag = ANY(p_dcb_tags) AND global_position > p_expected_position LIMIT 1
        // against the (tag, global_position) primary key. expectedPosition is always
        // SeededHead here, so this is a seek to the tail of one tag's B-tree range, not a
        // scan over its history -- a tag with StoreSize/100 rows (e.g. "order":"1", which
        // EventPlan.Build also gives to the single event below) costs the same index descent
        // as a tag with none ("never":"used", the original value here). Both are O(log n) in
        // table size, so neither answers "does the check get slower as history accumulates" --
        // it structurally can't, because the check only ever looks past expectedPosition, and
        // nothing has been appended past it. Kept as a real, existing tag anyway: it is the
        // more honest input for what this class measures (append-time DCB check cost across
        // StoreSize) even though the predicate's shape means that cost stays flat by design,
        // which is itself an answer to Q2(b) -- not a harness gap to work around further.
        _realHistoryQuery = DcbQuery.ByTags(new EventTag("order", "1"));
        return Task.CompletedTask;
    }

    [GlobalSetup(Target = nameof(AppendWithDcbCheck))]
    public Task SetupAppendWithDcbCheck() => InitAsync(AppendWithDcbCheck, StoreSize);

    [Benchmark, BenchmarkCategory(Categories.Append)]
    public Task<IReadOnlyCollection<IEventEnvelope>> AppendWithDcbCheck()
        => Backend.AppendAsync(_single, dcbQuery: _realHistoryQuery, expectedPosition: SeededHead);
}
