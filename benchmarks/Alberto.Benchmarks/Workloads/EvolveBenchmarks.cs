using Alberto.Benchmarks.Harness;
using BenchmarkDotNet.Attributes;

namespace Alberto.Benchmarks.Workloads;

/// <summary>
/// Folding through the dispatch table the source generator writes for a partial evolver, against
/// the one <c>EvolverDispatcher</c> builds by reflection and <c>Expression.Compile</c>. Under the
/// JIT the two should be indistinguishable; the generated one exists for Native AOT, where the
/// compiled expression is interpreted. CPU-only, so BenchmarkDotNet's default job rather than
/// <see cref="BenchmarkConfig"/>'s IO-tuned one.
/// </summary>
[MemoryDiagnoser]
public class EvolveBenchmarks
{
    [Params(1_000)]
    public int Events;

    private IEvent[] _history = [];
    private readonly Evolver<BenchState> _reflected = new ReflectedEvolver();
    private readonly Evolver<BenchState> _generated = new GeneratedEvolver();

    [GlobalSetup]
    public void Setup() => _history = Enumerable.Range(0, Events)
        .Select(i => i % 2 == 0 ? (IEvent)new BenchOpened(i) : new BenchDeposited(i))
        .ToArray();

    [Benchmark(Baseline = true), BenchmarkCategory(Categories.Evolve)]
    public BenchState Reflected() => Fold(_reflected);

    [Benchmark, BenchmarkCategory(Categories.Evolve)]
    public BenchState Generated() => Fold(_generated);

    private BenchState Fold(Evolver<BenchState> evolver)
    {
        var state = new BenchState();
        foreach (var e in _history) state = evolver.Evolve(state, e);
        return state;
    }
}

[EventType("bench-opened")]
public sealed record BenchOpened(int Value) : IEvent;

[EventType("bench-deposited")]
public sealed record BenchDeposited(int Value) : IEvent;

public sealed class BenchState
{
    public int Opened;
    public long Total;
}

public sealed class ReflectedEvolver : Evolver<BenchState>,
    IEvolve<BenchState, BenchOpened>, IEvolve<BenchState, BenchDeposited>
{
    public BenchState Apply(BenchState state, BenchOpened e) { state.Opened++; return state; }
    public BenchState Apply(BenchState state, BenchDeposited e) { state.Total += e.Value; return state; }
}

public sealed partial class GeneratedEvolver : Evolver<BenchState>,
    IEvolve<BenchState, BenchOpened>, IEvolve<BenchState, BenchDeposited>
{
    public BenchState Apply(BenchState state, BenchOpened e) { state.Opened++; return state; }
    public BenchState Apply(BenchState state, BenchDeposited e) { state.Total += e.Value; return state; }
}
