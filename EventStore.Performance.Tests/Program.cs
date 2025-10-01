using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using EventStore.Performance.Tests;

var config = ManualConfig.Create(DefaultConfig.Instance)
    .WithOptions(ConfigOptions.DisableOptimizationsValidator)
    .AddExporter(HtmlExporter.Default)
    .AddExporter(MarkdownExporter.GitHub)
    .AddExporter(CsvExporter.Default)
    .AddExporter(JsonExporter.Full)
    .AddLogger(ConsoleLogger.Default)
    .WithSummaryStyle(BenchmarkDotNet.Reports.SummaryStyle.Default.WithRatioStyle(BenchmarkDotNet.Columns.RatioStyle.Trend));

Console.WriteLine("🚀 Starting Alberto Event Store Performance Benchmarks");
Console.WriteLine();

BenchmarkRunner.Run<EventStoreBenchmarks>(config);
