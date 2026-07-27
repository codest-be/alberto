using System.Diagnostics;
using Alberto.Dcb;
using Alberto.Dcb.Benchmarks.Harness;
using Alberto.Dcb.Postgres;
using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Alberto.Dcb.Benchmarks.Workloads;

/// <summary>
/// Read throughput against PostgreSQL, at three store sizes.
///
/// Two cases here are the ones the retired InMemory suite never had. TailRead models the
/// polling steady state — reading just behind the head takes a different plan than reading
/// from position 0, and it is the read Alberto performs most often in production.
/// BoundaryRead models the small selective query before a decision, which is the latency a
/// user actually feels.
/// </summary>
public abstract class QueryBenchmarkBase
{
    protected const int PageSize = 500;

    protected NpgsqlDataSource DataSource = null!;
    protected PostgresEventStoreBackend Backend = null!;
    protected long Head;

    [Params(StoreSizes.Small, StoreSizes.Medium, StoreSizes.Large)]
    public int StoreSize { get; set; }

    /// <summary>
    /// Builds the store and warms <paramref name="measured"/> — and only it.
    ///
    /// Every concrete class calls this from one <c>[GlobalSetup(Target = ...)]</c> per benchmark
    /// method, passing the method that setup targets. See <see cref="Warmup"/> for why warming
    /// this class's *other* methods is actively harmful rather than merely wasteful.
    /// </summary>
    protected async Task InitAsync(Func<Task> measured)
    {
        var database = await BenchmarkDatabase.Instance;
        var connectionString = await database.CloneAsync(StoreSize, GetType().Name);

        DataSource = NpgsqlDataSource.Create(connectionString);
        Backend = new PostgresEventStoreBackend(DataSource);
        Head = await Backend.GetLastPositionAsync();

        await OnSetupAsync();

        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < Warmup.Invocations && elapsed.Elapsed < Warmup.Budget; i++)
        {
            await measured();
        }
    }

    protected virtual Task OnSetupAsync() => Task.CompletedTask;

    // Reads do not mutate, so unlike the append family these need no iteration cleanup.
    [GlobalCleanup]
    public async Task Cleanup() => await DataSource.DisposeAsync();
}

/// <summary>Read throughput against PostgreSQL, at three store sizes.</summary>
[Config(typeof(BenchmarkConfig))]
public class QueryBenchmarks : QueryBenchmarkBase
{
    private DcbQuery _byType = null!;
    private DcbQuery _byTag = null!;
    private DcbQuery _byTypeAndTag = null!;
    private DcbQuery _boundary = null!;

    protected override Task OnSetupAsync()
    {
        // Built once: DcbQuery construction is not what these cases measure.
        _byType = DcbQuery.ByTypes("order-placed");
        _byTag = DcbQuery.ByTags(new EventTag("order", "42"));
        _byTypeAndTag = DcbQuery.For("order", "42").WithTypes("order-placed");
        _boundary = DcbQuery.For("order", "7");

        return Task.CompletedTask;
    }

    [GlobalSetup(Target = nameof(StreamAllFromZero))]
    public Task SetupStreamAllFromZero() => InitAsync(StreamAllFromZero);

    [GlobalSetup(Target = nameof(TailRead))]
    public Task SetupTailRead() => InitAsync(TailRead);

    [GlobalSetup(Target = nameof(StreamByType))]
    public Task SetupStreamByType() => InitAsync(StreamByType);

    [GlobalSetup(Target = nameof(StreamByTag))]
    public Task SetupStreamByTag() => InitAsync(StreamByTag);

    [GlobalSetup(Target = nameof(StreamByTypeAndTag))]
    public Task SetupStreamByTypeAndTag() => InitAsync(StreamByTypeAndTag);

    [GlobalSetup(Target = nameof(BoundaryRead))]
    public Task SetupBoundaryRead() => InitAsync(BoundaryRead);

    [GlobalSetup(Target = nameof(GetLastPosition))]
    public Task SetupGetLastPosition() => InitAsync(GetLastPosition);

    [GlobalSetup(Target = nameof(GetStableHead))]
    public Task SetupGetStableHead() => InitAsync(GetStableHead);

    /// <summary>Full catch-up from the beginning of the log.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory(Categories.Query, Categories.Smoke)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllFromZero()
        => Backend.StreamAllAsync(afterPosition: 0, limit: PageSize);

    /// <summary>
    /// The polling steady state — reading the page just behind the head. Alberto's most
    /// frequent read in production, and a different query plan than reading from zero.
    /// The retired InMemory suite had no equivalent.
    /// </summary>
    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> TailRead()
        => Backend.StreamAllAsync(afterPosition: Math.Max(0, Head - PageSize), limit: PageSize);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByType()
        => Backend.StreamAsync(_byType, afterPosition: 0, limit: PageSize);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByTag()
        => Backend.StreamAsync(_byTag, afterPosition: 0, limit: PageSize);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByTypeAndTag()
        => Backend.StreamAsync(_byTypeAndTag, afterPosition: 0, limit: PageSize);

    /// <summary>
    /// A small selective read before a decision — the latency a user actually feels.
    /// Also absent from the retired suite.
    /// </summary>
    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> BoundaryRead()
        => Backend.StreamAsync(_boundary, afterPosition: 0, limit: 50);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<long> GetLastPosition()
        => Backend.GetLastPositionAsync();

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<long> GetStableHead()
        => Backend.GetStableHeadAsync(afterPosition: Math.Max(0, Head - PageSize));
}

/// <summary>
/// Tag-union reads — the DISTINCT-before-LIMIT over-scan path. Its own class so the
/// UnionTags axis does not multiply every other query case for no signal.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class MultiTagQueryBenchmarks : QueryBenchmarkBase
{
    private DcbQuery _byMultiTag = null!;

    [Params(2, 8)]
    public int UnionTags { get; set; }

    protected override Task OnSetupAsync()
    {
        _byMultiTag = DcbQuery
            .ByTags([.. Enumerable.Range(1, UnionTags)
                .Select(i => new EventTag("order", i.ToString()))])
            .AsUnion();

        return Task.CompletedTask;
    }

    [GlobalSetup(Target = nameof(StreamByMultiTag))]
    public Task SetupStreamByMultiTag() => InitAsync(StreamByMultiTag);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByMultiTag()
        => Backend.StreamAsync(_byMultiTag, afterPosition: 0, limit: PageSize);
}
