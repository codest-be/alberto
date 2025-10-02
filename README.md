# Alberto EventStore

A high-performance event store library for .NET with multi-tenant and multi-schema support.

## Features

- **Multi-Backend Architecture**: In-memory and PostgreSQL implementations
- **Multi-Tenant Support**: Isolated event streams per tenant
- **Multi-Schema Support**: Schema-based logical separation in PostgreSQL
- **Optimistic Concurrency**: Consistency boundaries with expected event IDs
- **High Performance**: Optimized bulk operations and connection pooling
- **Docker Integration**: Full Testcontainers support for testing

## Testing

The project uses a two-tier testing approach:

### Fast Feedback Tests (`EventStore.Tests`)
- **109 tests** running in ~3 seconds
- Unit and integration tests for correctness
- Runs on every push/PR for immediate feedback
- Command: `dotnet test EventStore.Tests`

### Performance Analysis (`EventStore.Performance.Tests`)
- **78 benchmarks** using BenchmarkDotNet
- Comprehensive performance analysis and regression detection
- Separate CI pipeline to preserve GitHub Actions minutes
- Command: `dotnet run --project EventStore.Performance.Tests --configuration Release`

## Performance

Recent benchmarks show excellent performance characteristics:

- **InMemory**: Single event append ~30μs, 1000-event batch ~1.2ms
- **PostgreSQL**: Single event append ~0.9ms, 1000-event batch ~18.5ms
- **Scalability**: Performance gap decreases with larger batches (15x vs 30x)

See [Performance Tests README](EventStore.Performance.Tests/README.md) for detailed benchmarks.

## CI/CD

- **Main Pipeline**: Fast build and test on every push/PR (~1-2 minutes)
- **Performance Pipeline**: Weekly performance analysis and regression detection (manual trigger available)