using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using Dapper;
using EventStore;
using EventStore.Events;
using EventStore.InMemory;
using EventStore.MultiTenant;
using EventStore.Postgres;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EventStore.Performance.Tests;

[Config(typeof(Config))]
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
[RankColumn]
public class EventStoreBenchmarks
{
    private class Config : ManualConfig
    {
        public Config()
        {
            // Simplified professional benchmark configuration
            AddJob(Job.Default
                .WithId("Baseline")
                .AsBaseline());

            // Add a second job with enhanced statistical sampling
            AddJob(Job.Default
                .WithId("Optimized")
                .WithInvocationCount(96)  // Multiple of 16 (UnrollFactor)
                .WithIterationCount(15)
                .WithWarmupCount(5));

            WithOptions(ConfigOptions.DisableOptimizationsValidator);
            
            // Add statistical columns for better analysis
            AddColumn(StatisticColumn.StdDev);
            AddColumn(StatisticColumn.Error);
        }
    }

    private IEventStoreBackend _inMemoryBackend = null!;
    private IEventStoreBackend _postgresBackend = null!;
    private IEventStoreBackend _postgresPooledBackend = null!;
    private IEventStoreBackend? _localhostPostgresBackend = null; // Optional localhost comparison
    private PostgreSqlContainer _postgreSqlContainer = null!;
    private readonly Tenant _tenant = new("benchmark-tenant");

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var inMemoryLogger = loggerFactory.CreateLogger<InMemoryEventStoreBackend>();
        _inMemoryBackend = new InMemoryEventStoreBackend(inMemoryLogger);

        _postgreSqlContainer = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("alberto_benchmarks")
            .WithUsername("benchmark_user")
            .WithPassword("benchmark_password")
            .WithCleanUp(true)
            // Optimize PostgreSQL for benchmarks
            .WithCommand("-c", "shared_buffers=256MB")
            .WithCommand("-c", "work_mem=16MB")
            .WithCommand("-c", "wal_buffers=16MB")
            .WithCommand("-c", "checkpoint_completion_target=0.9")
            .WithCommand("-c", "max_wal_size=2GB")
            .WithCommand("-c", "min_wal_size=1GB")
            .WithCommand("-c", "random_page_cost=1.1")
            .WithCommand("-c", "synchronous_commit=off") // Better for benchmarks
            .WithCommand("-c", "fsync=off") // Better for benchmarks (not for production!)
            .WithCommand("-c", "full_page_writes=off") // Better for benchmarks
            .Build();

        await _postgreSqlContainer.StartAsync();

        var options = Options.Create(new PostgresEventStoreOptions
        {
            ConnectionString = _postgreSqlContainer.GetConnectionString(),
            Schema = "benchmark"
        });

        await RunMigrations(options.Value);

        var postgresLogger = loggerFactory.CreateLogger<PostgresEventStoreBackend>();
        _postgresBackend = new PostgresEventStoreBackend(options, postgresLogger);

        // Create enhanced connection pooled version with optimized settings
        var pooledConnectionString = _postgreSqlContainer.GetConnectionString() +
            ";Pooling=true;MinPoolSize=5;MaxPoolSize=20;ConnectionLifeTime=300;CommandTimeout=30;ApplicationName=alberto_benchmarks;";
        var pooledOptions = Options.Create(new PostgresEventStoreOptions
        {
            ConnectionString = pooledConnectionString,
            Schema = "benchmark"
        });
        _postgresPooledBackend = new PostgresEventStoreBackend(pooledOptions, postgresLogger);

        // Try to setup localhost PostgreSQL for comparison (optional)
        try
        {
            var localhostConnectionString = "Host=localhost;Port=5432;Database=alberto_localhost;Username=postgres;Password=postgres;";
            var localhostOptions = Options.Create(new PostgresEventStoreOptions
            {
                ConnectionString = localhostConnectionString,
                Schema = "benchmark"
            });

            // Test connection and setup schema
            await using var testConnection = new NpgsqlConnection(localhostConnectionString);
            await testConnection.OpenAsync();
            await testConnection.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS benchmark");

            var localhostLogger = loggerFactory.CreateLogger<PostgresEventStoreBackend>();
            _localhostPostgresBackend = new PostgresEventStoreBackend(localhostOptions, localhostLogger);

            // Run migrations for localhost
            await RunMigrations(localhostOptions.Value);

            Console.WriteLine("✅ Localhost PostgreSQL available for comparison benchmarks");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Localhost PostgreSQL not available: {ex.Message}");
            _localhostPostgresBackend = null;
        }

        // Pre-populate with some test data to avoid setup issues in read benchmarks
        try
        {
            var testEvents = Enumerable.Range(0, 10)
                .Select(i => new EventToPersist
                {
                    EventType = new EventType("benchmark-event"),
                    EventJson = $"{{ \"message\": \"test data {i}\" }}",
                    Tags = [new EventTag("stream", "benchmark-read-stream")],
                    Metadata = new Dictionary<string, string> { ["source"] = "global-setup" },
                    Created = DateTimeOffset.UtcNow
                })
                .ToArray();

            await _postgresBackend.Append(_tenant, testEvents, null, null);
            await _inMemoryBackend.Append(_tenant, testEvents, null, null);
            Console.WriteLine($"Pre-populated {testEvents.Length} events during global setup");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to pre-populate test data: {ex.Message}");
            throw;
        }
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _postgreSqlContainer.StopAsync();
        await _postgreSqlContainer.DisposeAsync();
    }

    [Benchmark]
    public async Task AppendSingleEvent_InMemory()
    {
        var streamId = Guid.NewGuid().ToString();
        var events = new[]
        {
            new EventToPersist
            {
                EventType = new EventType("benchmark-event"),
                EventJson = "{ \"message\": \"benchmark data\" }",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string> { ["source"] = "benchmark" },
                Created = DateTimeOffset.UtcNow
            }
        };

        await _inMemoryBackend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    public async Task AppendSingleEvent_Postgres()
    {
        var streamId = Guid.NewGuid().ToString();
        var events = new[]
        {
            new EventToPersist
            {
                EventType = new EventType("benchmark-event"),
                EventJson = "{ \"message\": \"benchmark data\" }",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string> { ["source"] = "benchmark" },
                Created = DateTimeOffset.UtcNow
            }
        };

        await _postgresBackend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(1000)]
    public async Task AppendMultipleEvents_InMemory(int eventCount)
    {
        var streamId = Guid.NewGuid().ToString();
        var events = Enumerable.Range(0, eventCount)
            .Select(i => new EventToPersist
            {
                EventType = new EventType("benchmark-event"),
                EventJson = $"{{ \"message\": \"benchmark data {i}\" }}",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string> { ["source"] = "benchmark", ["index"] = i.ToString() },
                Created = DateTimeOffset.UtcNow
            })
            .ToArray();

        await _inMemoryBackend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(1000)]
    public async Task AppendMultipleEvents_Postgres(int eventCount)
    {
        var streamId = Guid.NewGuid().ToString();
        var events = Enumerable.Range(0, eventCount)
            .Select(i => new EventToPersist
            {
                EventType = new EventType("benchmark-event"),
                EventJson = $"{{ \"message\": \"benchmark data {i}\" }}",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string> { ["source"] = "benchmark", ["index"] = i.ToString() },
                Created = DateTimeOffset.UtcNow
            })
            .ToArray();

        await _postgresBackend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(10)]
    [Arguments(50)]
    public async Task BulkThresholdTest_Postgres(int threshold)
    {
        var options = Options.Create(new PostgresEventStoreOptions
        {
            ConnectionString = _postgreSqlContainer.GetConnectionString(),
            Schema = "benchmark",
            BulkInsertThreshold = threshold
        });

        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<PostgresEventStoreBackend>();
        var backend = new PostgresEventStoreBackend(options, logger);

        var streamId = Guid.NewGuid().ToString();
        var eventCount = 20; // Fixed count to test threshold crossing
        var events = Enumerable.Range(0, eventCount)
            .Select(i => new EventToPersist
            {
                EventType = new EventType("benchmark-event"),
                EventJson = $"{{ \"message\": \"threshold test {i}\" }}",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string> { ["threshold"] = threshold.ToString() },
                Created = DateTimeOffset.UtcNow
            })
            .ToArray();

        await backend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    public async Task AppendSingleEvent_PostgresPooled()
    {
        var streamId = Guid.NewGuid().ToString();
        var events = new[]
        {
            new EventToPersist
            {
                EventType = new EventType("benchmark-event"),
                EventJson = "{ \"message\": \"benchmark data pooled\" }",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string> { ["source"] = "benchmark-pooled" },
                Created = DateTimeOffset.UtcNow
            }
        };

        await _postgresPooledBackend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task AppendMultipleEvents_PostgresPooled(int eventCount)
    {
        var streamId = Guid.NewGuid().ToString();
        var events = Enumerable.Range(0, eventCount)
            .Select(i => new EventToPersist
            {
                EventType = new EventType("benchmark-event"),
                EventJson = $"{{ \"message\": \"pooled benchmark data {i}\" }}",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string> { ["source"] = "benchmark-pooled", ["index"] = i.ToString() },
                Created = DateTimeOffset.UtcNow
            })
            .ToArray();

        await _postgresPooledBackend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    public async Task ReadStream_InMemory()
    {
        var streamId = "benchmark-read-stream";
        var query = new StreamQuery(
            tags: [new EventTag("stream", streamId)]
        );
        await _inMemoryBackend.Stream(_tenant, query, 100);
    }

    [Benchmark]
    public async Task ReadStream_Postgres()
    {
        var streamId = "benchmark-read-stream";

        // Enhanced setup with detailed validation for isolated BenchmarkDotNet processes
        await EnsurePostgresSchemaAndData(async () =>
        {
            var setupEvents = Enumerable.Range(0, 10)
                .Select(i => new EventToPersist
                {
                    EventType = new EventType("benchmark-event"),
                    EventJson = $"{{ \"message\": \"read benchmark data {i}\" }}",
                    Tags = [new EventTag("stream", streamId)],
                    Metadata = new Dictionary<string, string> { ["source"] = "read-benchmark" },
                    Created = DateTimeOffset.UtcNow
                })
                .ToArray();

            await _postgresBackend.Append(_tenant, setupEvents, null, null);
        });

        var query = new StreamQuery(
            tags: [new EventTag("stream", streamId)]
        );

        await _postgresBackend.Stream(_tenant, query, 100);
    }

    [Benchmark]
    public async Task QueryEventsByType_InMemory()
    {
        var query = new StreamQuery(
            eventTypes: [new EventType("benchmark-event")]
        );

        await _inMemoryBackend.Stream(_tenant, query, 100);
    }

    [Benchmark]
    public async Task QueryEventsByType_Postgres()
    {
        // Enhanced setup with detailed validation for isolated BenchmarkDotNet processes
        await EnsurePostgresSchemaAndData(async () =>
        {
            var setupEvents = Enumerable.Range(0, 5)
                .Select(i => new EventToPersist
                {
                    EventType = new EventType("benchmark-event"),
                    EventJson = $"{{ \"message\": \"type query data {i}\" }}",
                    Tags = [new EventTag("source", "type-benchmark")],
                    Metadata = new Dictionary<string, string> { ["queryType"] = "type-based" },
                    Created = DateTimeOffset.UtcNow
                })
                .ToArray();

            await _postgresBackend.Append(_tenant, setupEvents, null, null);
        });

        var query = new StreamQuery(
            eventTypes: [new EventType("benchmark-event")]
        );

        await _postgresBackend.Stream(_tenant, query, 100);
    }

    [Benchmark]
    [Arguments(50, 100)] // 50ms timeout, 100 event threshold
    [Arguments(100, 50)] // 100ms timeout, 50 event threshold
    public async Task TimeBatchedAppend_Postgres(int timeoutMs, int eventThreshold)
    {
        // Simulate time-based batching: collect events until timeout OR threshold reached
        var events = new List<IEventToPersist>();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            // Simulate collecting events over time
            for (int i = 0; i < eventThreshold * 2; i++) // Generate more than threshold
            {
                // Check timeout condition
                if (cts.Token.IsCancellationRequested)
                    break;

                events.Add(new EventToPersist
                {
                    EventType = new EventType("time-batched-event"),
                    EventJson = $"{{ \"batchIndex\": {i}, \"timestamp\": \"{DateTimeOffset.UtcNow:O}\" }}",
                    Tags = [new EventTag("batch", "time-based")],
                    Metadata = new Dictionary<string, string>
                    {
                        ["timeout"] = timeoutMs.ToString(),
                        ["threshold"] = eventThreshold.ToString(),
                        ["collected"] = events.Count.ToString()
                    },
                    Created = DateTimeOffset.UtcNow
                });

                // Break if we hit the event threshold
                if (events.Count >= eventThreshold)
                    break;

                // Simulate small delay between event collection
                await Task.Delay(1, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout reached, proceed with collected events
        }

        // Benchmark the actual batch append operation
        if (events.Count > 0)
        {
            await _postgresBackend.Append(_tenant, events, null, null);
        }
    }

    [Benchmark]
    public async Task AppendSingleEvent_LocalhostPostgres()
    {
        if (_localhostPostgresBackend == null)
            throw new InvalidOperationException("Localhost PostgreSQL not available");

        var streamId = Guid.NewGuid().ToString();
        var events = new[]
        {
            new EventToPersist
            {
                EventType = new EventType("localhost-benchmark-event"),
                EventJson = "{ \"message\": \"localhost benchmark data\" }",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string> { ["source"] = "localhost-benchmark" },
                Created = DateTimeOffset.UtcNow
            }
        };

        await _localhostPostgresBackend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    [Arguments(100)]
    public async Task AppendMultipleEvents_LocalhostPostgres(int eventCount)
    {
        if (_localhostPostgresBackend == null)
            throw new InvalidOperationException("Localhost PostgreSQL not available");

        var streamId = Guid.NewGuid().ToString();
        var events = Enumerable.Range(0, eventCount)
            .Select(i => new EventToPersist
            {
                EventType = new EventType("localhost-benchmark-event"),
                EventJson = $"{{ \"message\": \"localhost batch data {i}\" }}",
                Tags = [new EventTag("stream", streamId)],
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "localhost-benchmark",
                    ["index"] = i.ToString()
                },
                Created = DateTimeOffset.UtcNow
            })
            .ToArray();

        await _localhostPostgresBackend.Append(_tenant, events, null, null);
    }

    [Benchmark]
    [Arguments(1000, 1)]   // 1K events, 1 tag each (most common real-world scenario)
    [Arguments(1000, 2)]   // 1K events, 2 tags each
    [Arguments(10000, 2)]  // 10K events, 2 tags each (typical scale)
    [Arguments(10000, 5)]  // 10K events, 5 tags each (high end of realistic)
    [Arguments(100000, 2)] // 100K events, 2 tags each (production scale)
    [Arguments(100000, 5)] // 100K events, 5 tags each (stress test at scale)
    public async Task TagQueryPerformance_RealisticScale_Postgres(int eventCount, int tagsPerEvent)
    {
        var dataSetupId = $"tag-perf-{eventCount}-{tagsPerEvent}";

        // Setup test data with realistic tag patterns
        await EnsureTagPerformanceData(eventCount, tagsPerEvent, dataSetupId);

        // Test 1: Query events by single tag (most common pattern)
        var singleTagQuery = new StreamQuery(
            tags: [new EventTag("dataset", dataSetupId)]
        );

        await _postgresBackend.Stream(_tenant, singleTagQuery, maxCount: 1000);

        // Test 2: Query events requiring ALL tags (consistency boundary pattern)
        if (tagsPerEvent > 1)
        {
            var allTagsQuery = new StreamQuery(
                tags: [
                    new EventTag("dataset", dataSetupId),
                    new EventTag("category", "business")
                ]
            ).RequiringAllTags();

            await _postgresBackend.Stream(_tenant, allTagsQuery, maxCount: 1000);
        }

        // Test 3: Query events with ANY of multiple tags (broad search pattern)
        var anyTagsQuery = new StreamQuery(
            tags: [
                new EventTag("priority", "high"),
                new EventTag("priority", "critical"),
                new EventTag("category", "business")
            ]
        ); // Default is RequireAny

        await _postgresBackend.Stream(_tenant, anyTagsQuery, maxCount: 1000);
    }

    [Benchmark]
    [Arguments(1000, 1)]   // 1K events, 1 tag each
    [Arguments(1000, 2)]   // 1K events, 2 tags each
    [Arguments(10000, 2)]  // 10K events, 2 tags each (typical scale)
    [Arguments(10000, 5)]  // 10K events, 5 tags each (high end)
    [Arguments(100000, 2)] // 100K events, 2 tags each (production scale)
    [Arguments(100000, 5)] // 100K events, 5 tags each (stress test)
    public async Task CommonPattern_TenantEventTypeTags_Postgres(int eventCount, int tagsPerEvent)
    {
        var dataSetupId = $"common-pattern-{eventCount}-{tagsPerEvent}";

        // Setup test data with realistic tag patterns
        await EnsureTagPerformanceData(eventCount, tagsPerEvent, dataSetupId);

        // Test the MOST COMMON pattern: tenant + event_type + tags
        // Example: "get order_created events for order:123"
        var commonQuery = new StreamQuery(
            eventTypes: [new EventType("order-created")],
            tags: [new EventTag("dataset", dataSetupId)]
        );

        await _postgresBackend.Stream(_tenant, commonQuery, maxCount: 1000);

        // Test with multiple event types (also common)
        var multiTypeQuery = new StreamQuery(
            eventTypes: [new EventType("order-created"), new EventType("payment-processed")],
            tags: [new EventTag("dataset", dataSetupId)]
        );

        await _postgresBackend.Stream(_tenant, multiTypeQuery, maxCount: 1000);

        // Test event type + multiple tags (business logic queries)
        if (tagsPerEvent > 1)
        {
            var businessQuery = new StreamQuery(
                eventTypes: [new EventType("order-created")],
                tags: [
                    new EventTag("dataset", dataSetupId),
                    new EventTag("category", "business")
                ]
            ).RequiringAllTags();

            await _postgresBackend.Stream(_tenant, businessQuery, maxCount: 1000);
        }
    }

    [Benchmark]
    [Arguments(1000, 1)]
    [Arguments(10000, 2)]
    [Arguments(100000, 2)]
    public async Task TagQueryPerformance_RealisticScale_InMemory(int eventCount, int tagsPerEvent)
    {
        var dataSetupId = $"tag-perf-inmem-{eventCount}-{tagsPerEvent}";

        // Setup test data in memory
        await EnsureInMemoryTagPerformanceData(eventCount, tagsPerEvent, dataSetupId);

        // Same query patterns as Postgres version for comparison
        var singleTagQuery = new StreamQuery(
            tags: [new EventTag("dataset", dataSetupId)]
        );

        await _inMemoryBackend.Stream(_tenant, singleTagQuery, maxCount: 1000);

        if (tagsPerEvent > 1)
        {
            var allTagsQuery = new StreamQuery(
                tags: [
                    new EventTag("dataset", dataSetupId),
                    new EventTag("category", "business")
                ]
            ).RequiringAllTags();

            await _inMemoryBackend.Stream(_tenant, allTagsQuery, maxCount: 1000);
        }

        var anyTagsQuery = new StreamQuery(
            tags: [
                new EventTag("priority", "high"),
                new EventTag("priority", "critical"),
                new EventTag("category", "business")
            ]
        );

        await _inMemoryBackend.Stream(_tenant, anyTagsQuery, maxCount: 1000);
    }

    [Benchmark]
    public async Task ConnectionPoolStress_Postgres()
    {
        // Stress test connection pooling by making concurrent database operations
        var tasks = new List<Task>();
        var random = new Random();

        for (int i = 0; i < 10; i++) // 10 concurrent operations
        {
            tasks.Add(Task.Run(async () =>
            {
                var streamId = Guid.NewGuid().ToString();
                var eventCount = random.Next(1, 20); // Random batch size
                var events = Enumerable.Range(0, eventCount)
                    .Select(j => new EventToPersist
                    {
                        EventType = new EventType("pool-stress-event"),
                        EventJson = $"{{ \"concurrentOp\": {i}, \"eventIndex\": {j} }}",
                        Tags = [new EventTag("stress", "connection-pool")],
                        Metadata = new Dictionary<string, string>
                        {
                            ["threadId"] = Thread.CurrentThread.ManagedThreadId.ToString(),
                            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                        },
                        Created = DateTimeOffset.UtcNow
                    })
                    .ToArray();

                await _postgresPooledBackend.Append(_tenant, events, null, null);
            }));
        }

        await Task.WhenAll(tasks);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        var setupStreamId = "benchmark-read-stream";
        var setupEvents = Enumerable.Range(0, 50)
            .Select(i => new EventToPersist
            {
                EventType = new EventType("benchmark-event"),
                EventJson = $"{{ \"message\": \"setup data {i}\" }}",
                Tags = [new EventTag("stream", setupStreamId)],
                Metadata = new Dictionary<string, string> { ["source"] = "setup" },
                Created = DateTimeOffset.UtcNow
            })
            .ToArray();

        try
        {
            _inMemoryBackend.Append(_tenant, setupEvents, null, null).GetAwaiter().GetResult();

            if (_postgresBackend != _inMemoryBackend)
            {
                _postgresBackend.Append(_tenant, setupEvents, null, null).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Setup failed: {ex.Message}");
            throw;
        }
    }

    private async Task EnsurePostgresSchemaAndData(Func<Task> dataSetup)
    {
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = _postgreSqlContainer.GetConnectionString(),
            Schema = "benchmark"
        };

        try
        {
            Console.WriteLine($"[BENCHMARK SETUP] Starting schema validation for isolated process");

            // Step 1: Validate connection
            await ValidateConnection(options);

            // Step 2: Run migrations with validation
            await RunMigrationsWithValidation(options);

            // Step 3: Verify schema exists
            await VerifySchemaExists(options);

            // Step 4: Setup test data
            Console.WriteLine($"[BENCHMARK SETUP] Setting up test data");
            await dataSetup();

            Console.WriteLine($"[BENCHMARK SETUP] Schema and data setup completed successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BENCHMARK SETUP ERROR] Failed: {ex.Message}");
            Console.WriteLine($"[BENCHMARK SETUP ERROR] Stack trace: {ex.StackTrace}");
            throw new InvalidOperationException($"PostgreSQL benchmark setup failed: {ex.Message}", ex);
        }
    }

    private async Task ValidateConnection(PostgresEventStoreOptions options)
    {
        Console.WriteLine($"[BENCHMARK SETUP] Validating connection to: {MaskConnectionString(options.ConnectionString)}");

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();

        var version = await connection.QuerySingleAsync<string>("SELECT version()");
        Console.WriteLine($"[BENCHMARK SETUP] Connected to PostgreSQL: {version.Split(' ')[1]}");
    }

    private async Task RunMigrationsWithValidation(PostgresEventStoreOptions options)
    {
        Console.WriteLine($"[BENCHMARK SETUP] Running migrations for schema: {options.Schema}");

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();

        // Create schema first
        await connection.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {options.Schema}");
        Console.WriteLine($"[BENCHMARK SETUP] Schema '{options.Schema}' created/verified");

        // Load and execute migration
        var migrationSql = await LoadMigrationFromFile();
        var content = $"SET search_path TO {options.Schema}, public;\n\n{migrationSql}";

        var affectedRows = await connection.ExecuteAsync(content);
        Console.WriteLine($"[BENCHMARK SETUP] Migration executed, {affectedRows} statements affected");
    }

    private async Task VerifySchemaExists(PostgresEventStoreOptions options)
    {
        Console.WriteLine($"[BENCHMARK SETUP] Verifying events table exists in schema: {options.Schema}");

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();

        // Check if events table exists with expected structure
        var tableExists = await connection.QuerySingleAsync<bool>($@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables
                WHERE table_schema = @schema
                AND table_name = 'events'
            )", new { schema = options.Schema });

        if (!tableExists)
        {
            throw new InvalidOperationException($"Events table does not exist in schema '{options.Schema}'");
        }

        // Verify specific columns exist
        var columns = await connection.QueryAsync<string>($@"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema
            AND table_name = 'events'
            ORDER BY column_name", new { schema = options.Schema });

        var columnList = columns.ToList();
        var requiredColumns = new[] { "tenant_id", "position", "id", "event_type", "data", "tags", "metadata", "created_at" };

        foreach (var required in requiredColumns)
        {
            if (!columnList.Contains(required))
            {
                throw new InvalidOperationException($"Required column '{required}' missing from events table. Found columns: {string.Join(", ", columnList)}");
            }
        }

        Console.WriteLine($"[BENCHMARK SETUP] Events table verified with columns: {string.Join(", ", columnList)}");
    }

    private static string MaskConnectionString(string connectionString)
    {
        // Simple masking for logging - just show host and database
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"Host={builder.Host}:{builder.Port} Database={builder.Database}";
        }
        catch
        {
            return "[connection string]";
        }
    }

    private async Task RunMigrations(PostgresEventStoreOptions options)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {options.Schema}");

        var migrationSql = await LoadMigrationFromFile();
        var content = $"SET search_path TO {options.Schema}, public;\n\n{migrationSql}";
        await connection.ExecuteAsync(content);
    }

    private async Task<string> LoadMigrationFromFile()
    {
        var currentDirectory = AppContext.BaseDirectory;
        var solutionDirectory = Directory.GetParent(currentDirectory);

        while (solutionDirectory != null
               && !Directory.Exists(Path.Combine(solutionDirectory.FullName, "EventStore.Postgres")))
            solutionDirectory = solutionDirectory.Parent;

        if (solutionDirectory == null)
            throw new DirectoryNotFoundException(
                "Could not locate the solution root directory containing EventStore.Postgres");

        var migrationPath = Path.Combine(
            solutionDirectory.FullName,
            "EventStore.Postgres",
            "Migrations",
            "CreateEventStoreSchema.sql");

        if (!File.Exists(migrationPath))
            throw new FileNotFoundException($"Migration file not found: {migrationPath}");

        return await File.ReadAllTextAsync(migrationPath);
    }

    private async Task EnsureTagPerformanceData(int eventCount, int tagsPerEvent, string dataSetupId)
    {
        // Check if data already exists for this test case
        var existingQuery = new StreamQuery(
            tags: [new EventTag("dataset", dataSetupId)]
        );
        var existing = await _postgresBackend.Stream(_tenant, existingQuery, maxCount: 1);

        if (existing.Any())
        {
            // Data already exists for this configuration
            return;
        }

        Console.WriteLine($"[TAG BENCHMARK] Setting up {eventCount} events with {tagsPerEvent} tags each for dataset: {dataSetupId}");

        // Create realistic tag patterns
        var eventTypes = new[] { "order-created", "payment-processed", "item-shipped", "order-completed" };
        var categories = new[] { "business", "system", "user-action", "integration" };
        var priorities = new[] { "low", "medium", "high", "critical" };
        var sources = new[] { "web", "mobile", "api", "batch" };

        var events = new List<IEventToPersist>();
        var random = new Random(42); // Fixed seed for reproducible benchmarks

        for (int i = 0; i < eventCount; i++)
        {
            var tags = new List<EventTag>
            {
                new EventTag("dataset", dataSetupId) // Always include dataset identifier
            };

            // Add realistic tag combinations based on tagsPerEvent
            if (tagsPerEvent > 1 && tags.Count < tagsPerEvent)
            {
                tags.Add(new EventTag("category", categories[random.Next(categories.Length)]));
            }
            if (tagsPerEvent > 2 && tags.Count < tagsPerEvent)
            {
                tags.Add(new EventTag("priority", priorities[random.Next(priorities.Length)]));
            }
            if (tagsPerEvent > 3 && tags.Count < tagsPerEvent)
            {
                tags.Add(new EventTag("source", sources[random.Next(sources.Length)]));
            }
            if (tagsPerEvent > 4 && tags.Count < tagsPerEvent)
            {
                tags.Add(new EventTag("tenant", $"tenant-{random.Next(1, 10)}"));
            }

            events.Add(new EventToPersist
            {
                EventType = new EventType(eventTypes[random.Next(eventTypes.Length)]),
                EventJson = $"{{ \"index\": {i}, \"data\": \"tag performance test event\" }}",
                Tags = tags,
                Metadata = new Dictionary<string, string>
                {
                    ["benchmark"] = "tag-performance",
                    ["eventCount"] = eventCount.ToString(),
                    ["tagsPerEvent"] = tagsPerEvent.ToString()
                },
                Created = DateTimeOffset.UtcNow.AddSeconds(-i) // Spread events over time
            });

            // Batch inserts for better performance during setup
            if (events.Count == 1000 || i == eventCount - 1)
            {
                await _postgresBackend.Append(_tenant, events, null, null);
                events.Clear();

                if (i % 10000 == 0)
                {
                    Console.WriteLine($"[TAG BENCHMARK] Inserted {i + 1}/{eventCount} events");
                }
            }
        }

        Console.WriteLine($"[TAG BENCHMARK] Completed setup for {dataSetupId}");
    }

    private async Task EnsureInMemoryTagPerformanceData(int eventCount, int tagsPerEvent, string dataSetupId)
    {
        // Check if data already exists
        var existingQuery = new StreamQuery(
            tags: [new EventTag("dataset", dataSetupId)]
        );
        var existing = await _inMemoryBackend.Stream(_tenant, existingQuery, maxCount: 1);

        if (existing.Any())
        {
            return;
        }

        // Create the same data structure as PostgreSQL version for fair comparison
        var eventTypes = new[] { "order-created", "payment-processed", "item-shipped", "order-completed" };
        var categories = new[] { "business", "system", "user-action", "integration" };
        var priorities = new[] { "low", "medium", "high", "critical" };
        var sources = new[] { "web", "mobile", "api", "batch" };

        var events = new List<IEventToPersist>();
        var random = new Random(42); // Same seed as PostgreSQL version

        for (int i = 0; i < eventCount; i++)
        {
            var tags = new List<EventTag>
            {
                new EventTag("dataset", dataSetupId)
            };

            if (tagsPerEvent > 1 && tags.Count < tagsPerEvent)
            {
                tags.Add(new EventTag("category", categories[random.Next(categories.Length)]));
            }
            if (tagsPerEvent > 2 && tags.Count < tagsPerEvent)
            {
                tags.Add(new EventTag("priority", priorities[random.Next(priorities.Length)]));
            }
            if (tagsPerEvent > 3 && tags.Count < tagsPerEvent)
            {
                tags.Add(new EventTag("source", sources[random.Next(sources.Length)]));
            }
            if (tagsPerEvent > 4 && tags.Count < tagsPerEvent)
            {
                tags.Add(new EventTag("tenant", $"tenant-{random.Next(1, 10)}"));
            }

            events.Add(new EventToPersist
            {
                EventType = new EventType(eventTypes[random.Next(eventTypes.Length)]),
                EventJson = $"{{ \"index\": {i}, \"data\": \"tag performance test event\" }}",
                Tags = tags,
                Metadata = new Dictionary<string, string>
                {
                    ["benchmark"] = "tag-performance-inmem",
                    ["eventCount"] = eventCount.ToString(),
                    ["tagsPerEvent"] = tagsPerEvent.ToString()
                },
                Created = DateTimeOffset.UtcNow.AddSeconds(-i)
            });
        }

        // Insert all at once for in-memory (no batching needed)
        await _inMemoryBackend.Append(_tenant, events, null, null);
    }
}
