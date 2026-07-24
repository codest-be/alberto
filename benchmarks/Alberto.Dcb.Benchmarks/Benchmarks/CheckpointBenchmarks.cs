using Alberto.Dcb.InMemory;
using BenchmarkDotNet.Attributes;

namespace Alberto.Dcb.Benchmarks.Benchmarks;

/// <summary>
/// Measures checkpoint read/write throughput using the in-memory store.
///
/// Purpose
/// -------
/// Establishes the performance floor for checkpoint operations and provides a
/// before/after baseline for P2 and P0 checkpoint fixes:
///
///   P0.2  — CachingCheckpointStore stale cache after fence rejection
///            (the fix forces a DB read on the next GetAsync call; this benchmark
///            shows the cost of the cold-path read vs the cached hot path)
///
/// Implementation note
/// -------------------
/// CachingCheckpointStore is internal to Alberto.Dcb, so it cannot be benchmarked
/// directly from this project without an InternalsVisibleTo addition (recorded in
/// crossFileNeeds). InMemoryCheckpointStore is used as a proxy for the pure
/// in-memory hot-path cost, which is the lower bound all checkpoint implementations
/// must beat.
///
/// Multi-processor scenario
/// ------------------------
/// <see cref="FlushMultipleProcessors"/> simulates what CachingCheckpointStore does
/// on every timer tick: save one position per processor. The <see cref="ProcessorCount"/>
/// parameter covers single-processor deployments and typical multi-tenant fan-outs.
///
/// Run
/// ---
///   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --filter '*Checkpoint*'
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class CheckpointBenchmarks
{
    private InMemoryCheckpointStore _store = null!;
    private string[] _processorIds = null!;

    private const string SingleProcessorId = "benchmark-processor";
    private const long CheckpointPosition = 100_000L;

    /// <summary>
    /// Number of processors whose checkpoints are flushed together.
    /// Models a single processor (1), a small fanout (10), and a large multi-tenant
    /// deployment (100).
    /// </summary>
    [Params(1, 10, 100)]
    public int ProcessorCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _store = new InMemoryCheckpointStore();

        // Prime the single-processor entry so GetAsync returns a non-null value.
        await _store.SaveAsync(SingleProcessorId, CheckpointPosition);

        // Pre-build the processor id array for the multi-processor flush.
        _processorIds = Enumerable.Range(1, ProcessorCount)
            .Select(i => $"processor-{i}")
            .ToArray();

        // Prime all processor entries.
        foreach (var id in _processorIds)
        {
            await _store.SaveAsync(id, CheckpointPosition);
        }
    }

    /// <summary>
    /// Saves a single checkpoint position.
    /// Models the per-batch commit call in ControlLoop (hot path).
    /// Baseline for all other checkpoint benchmarks.
    /// </summary>
    [Benchmark(Baseline = true)]
    public Task SaveCheckpoint()
        => _store.SaveAsync(SingleProcessorId, CheckpointPosition + 1);

    /// <summary>
    /// Reads the current checkpoint position.
    /// Models the ControlLoop restart-position lookup (cold path, once per boot).
    /// </summary>
    [Benchmark]
    public Task<long?> GetCheckpoint()
        => _store.GetAsync(SingleProcessorId);

    /// <summary>
    /// Saves checkpoints for <see cref="ProcessorCount"/> processors in a sequential loop.
    /// Models what CachingCheckpointStore.FlushAsync does on every timer tick:
    /// drain all dirty processor entries to the underlying store.
    /// </summary>
    [Benchmark]
    public async Task FlushMultipleProcessors()
    {
        foreach (var id in _processorIds)
        {
            await _store.SaveAsync(id, CheckpointPosition + 1);
        }
    }
}
