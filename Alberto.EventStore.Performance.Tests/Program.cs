using Alberto.EventStore.Performance.Tests;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

ManualConfig config = ManualConfig.Create(DefaultConfig.Instance)
    .WithOptions(ConfigOptions.DisableOptimizationsValidator)
    .AddExporter(HtmlExporter.Default)
    .AddExporter(MarkdownExporter.GitHub)
    .AddExporter(CsvExporter.Default)
    .AddExporter(JsonExporter.Full)
    .AddLogger(ConsoleLogger.Default)
    .WithSummaryStyle(SummaryStyle.Default.WithRatioStyle(RatioStyle.Trend));

Console.WriteLine("🚀 Starting Alberto Event Store Performance Benchmarks");
Console.WriteLine();

BenchmarkRunner.Run<EventStoreBenchmarks>(config);