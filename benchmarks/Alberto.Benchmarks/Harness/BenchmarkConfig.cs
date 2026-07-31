using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace Alberto.Benchmarks.Harness;

/// <summary>Category names, used for --anyCategories filtering.</summary>
public static class Categories
{
    public const string Append = "append";
    public const string Query = "query";

    /// <summary>
    /// The smallest possible subset, run on every PR with --job dry. It proves the suite
    /// still compiles and executes; it measures nothing useful.
    /// </summary>
    public const string Smoke = "smoke";
}

/// <summary>
/// Shared configuration for every workload.
///
/// RunStrategy.Monitoring rather than Throughput: the work is IO-dominated, so BDN's default
/// statistical machinery mostly burns wall-clock without sharpening the result. MemoryDiagnoser
/// stays on because allocation counts remain near-deterministic even when timing is noisy,
/// which is what keeps allocation regressions detectable at all.
/// </summary>
public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(2)
            .WithIterationCount(10)
            .WithInvocationCount(1)
            .WithUnrollFactor(1));

        AddDiagnoser(MemoryDiagnoser.Default);

        // JsonExporter.Full is what the compare tool imports; the others are for humans.
        AddExporter(JsonExporter.Full);
        AddExporter(MarkdownExporter.GitHub);

        AddColumnProvider(DefaultColumnProviders.Instance);
        AddLogger(ConsoleLogger.Default);
    }
}
