using BenchmarkDotNet.Running;

// Run everything:
//   dotnet run -c Release --project benchmarks/Alberto.Benchmarks
//
// Run one family:
//   dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --anyCategories=append
//   dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --anyCategories=query
//
// Smoke run (proves it compiles and executes; measures nothing):
//   dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --job dry --anyCategories=smoke
//
// Against an existing Postgres instead of Testcontainers:
//   ALBERTO_BENCH_POSTGRES="Host=...;Database=...;Username=...;Password=..." dotnet run ...

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

// Program is referenced by FromAssembly above; the partial declaration keeps top-level
// statements and the type reference compatible.
public partial class Program;
