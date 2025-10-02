# EventStore Performance Tests

This project contains comprehensive performance testing for the Alberto EventStore implementation using BenchmarkDotNet
with Docker-based PostgreSQL testing.

## BenchmarkDotNet Micro-benchmarks (`EventStoreBenchmarks.cs`)

Measures precise performance characteristics of individual operations using **Testcontainers** for isolated PostgreSQL
testing:

- **Single Event Append**: InMemory vs PostgreSQL backend comparison with connection pooling
- **Batch Event Append**: Tests with 10, 100, and 1000 events per batch
- **Bulk Threshold Optimization**: Tests different bulk insert thresholds (1, 5, 10, 50 events)
- **Stream Reading**: Performance of streaming events from storage by stream tag
- **Event Querying**: Query performance by event type
- **Connection Pooling**: PostgreSQL performance with and without connection pooling

### Running Benchmarks

**For accurate performance measurements, always build and run in Release mode:**

```bash
cd EventStore.Performance.Tests

# Build the entire solution in Release mode
dotnet build -c Release

# Run the benchmarks (automatically spins up Docker PostgreSQL)
dotnet run -c Release

# Or run from solution root
dotnet run --project EventStore.Performance.Tests --configuration Release
```

**Prerequisites:**

- Docker Desktop must be running
- No additional setup required - benchmarks automatically manage PostgreSQL containers

**Quick testing (Debug mode):**

```bash
# For development/testing only - results won't be accurate
dotnet run
```

### CI/CD Integration

Performance tests run in a **separate GitHub Actions pipeline** to avoid consuming excessive CI minutes:

- **Triggers**: Manual dispatch, releases, weekly schedule (Monday 6 AM UTC)
- **Pipeline**: `.github/workflows/performance.yml`
- **Duration**: 10-30 minutes depending on system load
- **Artifacts**: Performance reports retained for 30 days
- **Separation**: Main CI runs only fast unit tests for immediate feedback

The benchmarks will generate detailed reports showing:

- Mean execution time and standard deviation
- Memory allocation patterns
- Statistical analysis (min, max, median, percentiles)
- Comparison between InMemory, PostgreSQL, and PostgreSQL Pooled backends
- Bulk insert threshold optimization results

## Dependencies

- **BenchmarkDotNet 0.15.4**: Micro-benchmarking framework
- **Microsoft.Extensions.Logging.Console 9.0.0**: Logging support
- **NBomber 6.1.1**: For future load testing scenarios
- **Testcontainers.PostgreSql 4.7.0**: Docker container management for isolated PostgreSQL testing
- **Respawn 6.2.1**: For future database cleanup scenarios

## Actual Performance Results (Latest Benchmarks)

### InMemory Backend

- **Single event append**: 29.53 μs (baseline)
- **10 events batch**: 53.35 μs
- **100 events batch**: 156.36 μs
- **1000 events batch**: 1,205.34 μs (~1.2ms)
- **Stream reading**: 942.65 μs
- **Event query**: 845.30 μs

### PostgreSQL Backend (Docker Optimized)

- **Single event append**: 878.00 μs (~0.9ms, **30x slower** than InMemory)
- **10 events batch**: 1,518.63 μs (~1.5ms, **28x slower**)
- **100 events batch**: 3,337.81 μs (~3.3ms, **21x slower**)
- **1000 events batch**: 18,529.34 μs (~18.5ms, **15x slower**)

### PostgreSQL with Connection Pooling

- **Single event append**: 967.07 μs (~1.0ms)
- **10 events batch**: 1,642.63 μs (~1.6ms)
- **100 events batch**: 3,536.14 μs (~3.5ms)

### Bulk Insert Threshold Optimization

- **Threshold 1**: 2,766.48 μs (20 events)
- **Threshold 5**: 2,375.42 μs (**optimal**, 14% faster)
- **Threshold 10**: 2,471.68 μs
- **Threshold 50**: 6,312.41 μs (overhead impact)

**Key Insights:**

- PostgreSQL performance gap **decreases with larger batches** (15x vs 30x)
- **Bulk threshold of 5 events** provides optimal performance
- Database optimizations reduced overhead from 65x to 30x slower

## Docker Integration

The benchmarks now use **Testcontainers** for fully automated PostgreSQL testing:

- **Automatic Container Management**: PostgreSQL containers are created and destroyed automatically
- **Optimized Configuration**: Uses PostgreSQL 17 Alpine with performance tuning:
    - `shared_buffers=256MB`
    - `work_mem=16MB`
    - `synchronous_commit=off` (benchmark-safe)
    - `fsync=off` (benchmark-safe)
- **Connection Pooling**: Tests both standard and pooled connections (5-20 connections)
- **No Manual Setup**: No external dependencies or manual database configuration required

**Container Details:**

- Image: `postgres:17-alpine`
- Database: `alberto_benchmarks`
- User: `benchmark_user`
- Automatic cleanup after tests complete

## Interpreting Results

### BenchmarkDotNet Metrics

- **Mean**: Average execution time
- **Error**: Half of 99.9% confidence interval
- **StdDev**: Standard deviation of measurements
- **Allocated**: Memory allocated per operation

### Key Performance Indicators

1. **Latency**: InMemory < 50μs, PostgreSQL < 1ms for single operations
2. **Memory**: Minimal allocations per operation (< 20KB for batch operations)
3. **Scalability**: Better performance ratio for larger batches (15x vs 30x gap)
4. **Backend Comparison**: InMemory significantly outperforms PostgreSQL as expected

## Troubleshooting

### Docker Requirements

- **Docker Desktop must be running** before starting benchmarks
- Ensure sufficient Docker memory allocation (> 2GB recommended)
- On Windows/Mac: Verify Docker Desktop is running and accessible

### Container Issues

- If containers fail to start, check Docker logs: `docker logs <container_id>`
- Restart Docker Desktop if experiencing connection issues
- Benchmarks automatically clean up containers on completion

### Benchmark Reliability

- **Always run in Release mode**: `dotnet run -c Release` for accurate results
- Close other applications to reduce system noise
- Allow sufficient warmup iterations (BenchmarkDotNet handles this automatically)
- Results may vary between runs due to system and Docker conditions
- First run may be slower due to Docker image downloads

### Performance Optimization

- For production use, consider setting `BulkInsertThreshold = 5` in PostgreSQL options
- Connection pooling shows mixed results - test with your specific workload
- Database tuning parameters in benchmarks are optimized for testing, not production