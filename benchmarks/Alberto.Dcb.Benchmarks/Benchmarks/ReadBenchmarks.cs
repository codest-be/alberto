using Alberto.Dcb;
using Alberto.Dcb.InMemory;
using BenchmarkDotNet.Attributes;

namespace Alberto.Dcb.Benchmarks.Benchmarks;

/// <summary>
/// Measures event-read throughput on the InMemory backend, simulating the catch-up
/// reads that the PollingConsumer performs on startup or after a processing lag.
///
/// Purpose
/// -------
/// Provides a before/after baseline for P2 read-path fixes:
///   SQL-1   — multi-tag DISTINCT-before-LIMIT over-scan (Postgres; InMemory exposes
///              the same algorithmic pattern via its full-scan HashSet collection)
///   SQL-6   — types-or-tags query driving from events table with double LEFT JOIN
///   PERF-3  — GetOrdinal by name per column per row on the Postgres reader
///   PERF-4  — EventTag.Parse on every row for already-valid DB data
///   PERF-8/9 — DcbQuery.Tags / WildcardPatterns recomputing LINQ per access
///
/// The InMemory backend mirrors the algorithmic structure of the Postgres backend
/// (inverted index per type, inverted index per tag, then position-set intersection/union),
/// so relative throughput differences measured here should predict Postgres hot-path
/// improvements, even though the absolute numbers differ.
///
/// Seed distribution
/// -----------------
/// SeedCount events, spread across 3 event types and 100 distinct order tags.
/// A PageSize of 500 events per read models a typical polling batch.
///
/// Run
/// ---
///   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --filter '*Read*'
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class ReadBenchmarks
{
    private InMemoryEventStoreBackend _backend = null!;

    // Pre-built queries — constructed once so per-access LINQ (PERF-8/9) is not
    // measured inside the benchmark invocation itself.
    private DcbQuery _byTypeQuery = null!;
    private DcbQuery _byTagQuery = null!;
    private DcbQuery _byTypeAndTagQuery = null!;
    private DcbQuery _byMultiTagQuery = null!;

    /// <summary>
    /// Total events seeded before benchmarks run. Tests both a warm cache and a
    /// realistic event-store size.
    /// </summary>
    [Params(1_000, 10_000)]
    public int SeedCount { get; set; }

    /// <summary>
    /// Maximum events returned per read call — mirrors the default PollingConsumer
    /// batch size.
    /// </summary>
    private const int PageSize = 500;

    [GlobalSetup]
    public async Task Setup()
    {
        _backend = new InMemoryEventStoreBackend();

        var eventTypes = new[]
        {
            new EventType("order-placed"),
            new EventType("order-cancelled"),
            new EventType("payment-received"),
        };
        var rng = new Random(42);

        for (var i = 0; i < SeedCount; i++)
        {
            var type = eventTypes[rng.Next(eventTypes.Length)];
            // 100 distinct order ids — realistic tag-fan-out for a busy service.
            var orderId = (i % 100 + 1).ToString();
            await _backend.Append(
            [
                new EventToPersist
                {
                    EventType = type,
                    // FromStorage skips regex — seed performance is not under test.
                    Tags = [EventTag.FromStorage("order", orderId)],
                    EventData = """{"seeded":true}""",
                },
            ]);
        }

        // Build queries once: avoids per-invocation LINQ + TagPattern construction.
        _byTypeQuery = DcbQuery.ByTypes("order-placed");

        _byTagQuery = DcbQuery.ByTags(EventTag.FromStorage("order", "42"));

        // Narrow (Intersect): events that are both "order-placed" AND tagged "order:42".
        _byTypeAndTagQuery = DcbQuery
            .For("order", "42")
            .WithTypes("order-placed");

        // Union across two tags: models a processor that handles two related aggregates.
        _byMultiTagQuery = DcbQuery.ByTags(
            EventTag.FromStorage("order", "1"),
            EventTag.FromStorage("order", "2"));
    }

    /// <summary>
    /// Full catch-up read: all events from position 0, bounded by PageSize.
    /// Baseline for all other read benchmarks.
    /// </summary>
    [Benchmark(Baseline = true)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAll()
        => _backend.StreamAll(afterPosition: 0, limit: PageSize);

    /// <summary>
    /// Filtered catch-up read by a single event type.
    /// Models a projection that only consumes one event type (common pattern).
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByType()
        => _backend.Stream(_byTypeQuery, afterPosition: 0, limit: PageSize);

    /// <summary>
    /// Filtered catch-up read by a single exact tag.
    /// Models a per-entity consistency boundary query (the most common DCB pattern).
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByTag()
        => _backend.Stream(_byTagQuery, afterPosition: 0, limit: PageSize);

    /// <summary>
    /// Filtered catch-up read by event type AND tag (intersection).
    /// The most selective path: events must satisfy both axes.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByTypeAndTag()
        => _backend.Stream(_byTypeAndTagQuery, afterPosition: 0, limit: PageSize);

    /// <summary>
    /// Filtered catch-up read across two distinct tags (union).
    /// Models a processor that spans two aggregates — exercises the DISTINCT-equivalent
    /// de-duplication path (SQL-1 analogue in InMemory: HashSet union + re-scan).
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByMultiTag()
        => _backend.Stream(_byMultiTagQuery, afterPosition: 0, limit: PageSize);
}
