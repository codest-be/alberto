using Alberto.Dcb;
using Alberto.Dcb.InMemory;
using BenchmarkDotNet.Attributes;

namespace Alberto.Dcb.Benchmarks.Benchmarks;

/// <summary>
/// Measures event-append throughput on the InMemory backend.
///
/// Purpose
/// -------
/// Provides a before/after baseline for P2 hot-path fixes:
///   PERF-2  — append interceptor chain pre-building (currently re-aggregates per append)
///   PERF-7  — JsonDocument lease that leaks an ArrayPool rental per append
///   PERF-4  — EventTag construction runs a validation regex; FromStorage skips it
///
/// The "40–50x DB write reduction" claim in the docs refers to BufferedCheckpointStore
/// coalescing checkpoint writes — see <see cref="CheckpointBenchmarks"/> for that path.
///
/// Setup note
/// ----------
/// Event objects and tags are pre-built in <see cref="Setup"/> so the benchmark measures
/// only the append hot-path, not object/string construction overhead.  A separate
/// <see cref="SingleAppend_WithTagValidation"/> case intentionally includes the EventTag
/// regex to baseline PERF-4 isolation.
///
/// Run
/// ---
///   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --filter '*Append*'
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class AppendBenchmarks
{
    private InMemoryEventStoreBackend _backend = null!;

    // Pre-built event instances reused across iterations to isolate append path cost.
    private IEventToPersist[] _batchEvents = null!;
    private IEventToPersist _singleEventPrebuilt = null!;

    // Separate event type + tag for the "with validation" and DCB-check benchmarks.
    private EventType _orderPlacedType;
    private EventTag _orderTag = default;

    // Query that targets a tag never used in any append, so the DCB conflict check
    // always takes the "no conflict" fast path without throwing across iterations.
    private DcbQuery _neverConflictingQuery = null!;

    /// <summary>
    /// Number of events per batch. Tests both common small batches and larger writes.
    /// </summary>
    [Params(10, 100)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _backend = new InMemoryEventStoreBackend();

        _orderPlacedType = new EventType("order-placed");

        // Use FromStorage to skip regex validation — measures the append path only.
        _orderTag = EventTag.FromStorage("order", "1");

        _singleEventPrebuilt = new EventToPersist
        {
            EventType = _orderPlacedType,
            Tags = [_orderTag],
            EventData = """{"orderId":"1","amount":99.99}""",
        };

        _batchEvents = new IEventToPersist[BatchSize];
        for (var i = 0; i < BatchSize; i++)
        {
            _batchEvents[i] = new EventToPersist
            {
                EventType = _orderPlacedType,
                // Distinct order ids so each event gets its own tag-index entry.
                Tags = [EventTag.FromStorage("order", (i + 1).ToString())],
                EventData = """{"orderId":"1","amount":9.99}""",
            };
        }

        // A tag never used in any append, so FindConflictPosition always returns null
        // (no conflict), keeping SingleAppend_WithDcbCheck stable across iterations.
        // The benchmark still exercises the full DCB-check code path.
        _neverConflictingQuery = DcbQuery.ByTags(EventTag.FromStorage("never", "used"));
    }

    /// <summary>
    /// Baseline: appends one event with no DCB conflict check.
    /// This is the minimum unit of work for any append.
    /// </summary>
    [Benchmark(Baseline = true)]
    public Task<IReadOnlyCollection<IEventEnvelope>> SingleAppend()
        => _backend.Append([_singleEventPrebuilt]);

    /// <summary>
    /// Appends a batch of events (parameterized by <see cref="BatchSize"/>) in one call.
    /// A well-behaved implementation should be sub-linear compared to N × SingleAppend.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyCollection<IEventEnvelope>> BatchAppend()
        => _backend.Append(_batchEvents);

    /// <summary>
    /// Single append where the EventTag is constructed inside the benchmark invocation —
    /// i.e., it includes the EventTag regex validation cost (PERF-4).
    /// Compare with <see cref="SingleAppend"/> to isolate the regex overhead.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyCollection<IEventEnvelope>> SingleAppend_WithTagValidation()
    {
        var evt = new EventToPersist
        {
            EventType = _orderPlacedType,
            Tags = [new EventTag("order", "1")],   // <-- regex fires here
            EventData = """{"orderId":"1","amount":99.99}""",
        };
        return _backend.Append([evt]);
    }

    /// <summary>
    /// Single append that includes a DCB conflict check (no-conflict fast path).
    /// Models the "append with optimistic concurrency" pattern on a boundary that has
    /// never received a matching event — i.e., the common happy path.
    ///
    /// The query targets a tag never written to the store so
    /// <c>FindConflictPosition</c> always returns null without throwing, keeping the
    /// benchmark result stable across iterations.  Compare with
    /// <see cref="SingleAppend"/> to isolate the conflict-check overhead.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyCollection<IEventEnvelope>> SingleAppend_WithDcbCheck()
        => _backend.Append([_singleEventPrebuilt], dcbQuery: _neverConflictingQuery, expectedPosition: 0);
}
