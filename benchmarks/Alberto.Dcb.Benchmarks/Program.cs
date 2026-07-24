using BenchmarkDotNet.Running;

// Run all benchmarks:
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks
//
// Run a specific benchmark class:
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --filter '*Append*'
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --filter '*Read*'
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --filter '*Checkpoint*'
//
// Quick smoke-run (no warmup, 1 iteration — confirms the code compiles and runs):
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --job dry

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
