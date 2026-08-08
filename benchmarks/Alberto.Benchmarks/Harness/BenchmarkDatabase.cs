using System.Collections.Concurrent;
using Alberto.Benchmarks.Core;
using Alberto.Postgres;
using Alberto.TestInfrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Alberto.Benchmarks.Harness;

/// <summary>
/// Owns the Postgres the whole benchmark process runs against.
///
/// Each store size is migrated and seeded ONCE into a template database; every benchmark
/// class then clones that template in its GlobalSetup. Cloning is a file copy inside
/// Postgres, so per-class setup costs about a second instead of re-seeding a million rows.
///
/// Mirrors tests/Alberto.Tests/Infrastructure/PostgresCluster.cs, including its
/// load-bearing pooling constraint.
/// </summary>
public sealed class BenchmarkDatabase : IAsyncDisposable
{
    private const string Image = "postgres:16-alpine";
    private const int MaxConnections = 200;
    private const int SeedBatchSize = 1_000;

    private static readonly Lazy<Task<BenchmarkDatabase>> Lazy = new(CreateAsync);

    private readonly PostgreSqlContainer? _container;
    private readonly string _adminConnectionString;
    private readonly ConcurrentDictionary<int, Lazy<Task>> _templates = new();
    private int _databaseCount;

    private BenchmarkDatabase(PostgreSqlContainer? container, string adminConnectionString)
    {
        _container = container;
        _adminConnectionString = adminConnectionString;
    }

    /// <summary>The process-wide instance. Started on first use.</summary>
    public static Task<BenchmarkDatabase> Instance => Lazy.Value;

    /// <summary>Recorded in the machine profile so results are keyed by what they ran against.</summary>
    public string PostgresImage => IsExternal ? "external" : Image;

    /// <summary>True when running against an external Postgres rather than the managed container.</summary>
    public bool IsExternal => _container is null;

    private static async Task<BenchmarkDatabase> CreateAsync()
    {
        // An external Postgres lets a tuned host be measured instead of a container.
        var external = Environment.GetEnvironmentVariable("ALBERTO_BENCH_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            return new BenchmarkDatabase(container: null, external);
        }

        var container = await ContainerStartup.StartNewAsync(
            () => new PostgreSqlBuilder(Image)
                .WithCommand("-c", $"max_connections={MaxConnections}")
                .Build());

        return new BenchmarkDatabase(container, container.GetConnectionString());
    }

    /// <summary>
    /// Returns a connection string to a fresh database cloned from the template for
    /// <paramref name="storeSize"/>, building and seeding that template on first use.
    /// </summary>
    /// <param name="storeSize">Number of events seeded into the template.</param>
    /// <param name="label">Names the database in server-side views; typically the benchmark class name.</param>
    public async Task<string> CloneAsync(int storeSize, string label)
    {
        await _templates.GetOrAdd(storeSize, size => new Lazy<Task>(() => BuildTemplateAsync(size))).Value;

        var database = NextDatabaseName(label);

        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""CREATE DATABASE "{database}" TEMPLATE "{TemplateName(storeSize)}" """;
            await command.ExecuteNonQueryAsync();
        }

        // Small pools: the server allows 200 connections and many classes run in one process.
        return ConnectionStringFor(database, b => b.MaxPoolSize = 10);
    }

    private async Task BuildTemplateAsync(int storeSize)
    {
        var template = TemplateName(storeSize);

        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""CREATE DATABASE "{template}" """;
            await command.ExecuteNonQueryAsync();
        }

        // Pooling MUST be off. A pooled connection is returned to the pool on close but its
        // physical session stays open, and CREATE DATABASE ... TEMPLATE refuses to run while
        // any session is connected to the source database.
        var buildConnectionString = ConnectionStringFor(template, b => b.Pooling = false);

        MigrationResult result;
        try
        {
            result = PostgresMigrator.Migrate(buildConnectionString, schema: null, singleTenant: true);
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException($"Migrating template '{template}' threw.", ex);
        }

        if (!result.Successful)
        {
            throw new InvalidOperationException($"Migrating template '{template}' failed.", result.Error);
        }

        await SeedAsync(buildConnectionString, storeSize);
    }

    private static async Task SeedAsync(string connectionString, int storeSize)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var backend = new PostgresEventStoreBackend(dataSource);

        var events = EventPlan.Build(storeSize, seed: 42);

        for (var offset = 0; offset < events.Count; offset += SeedBatchSize)
        {
            var batch = events.Skip(offset).Take(SeedBatchSize).ToArray();
            await backend.AppendAsync(batch);
        }

        // Without current statistics the planner picks different plans between runs and the
        // suite measures the planner's mood rather than the code.
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM ANALYZE";
        await command.ExecuteNonQueryAsync();
    }

    private static string TemplateName(int storeSize) => $"bench_tmpl_st_{storeSize}";

    private string ConnectionStringFor(string database, Action<NpgsqlConnectionStringBuilder>? configure = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = database };
        configure?.Invoke(builder);
        return builder.ConnectionString;
    }

    private string NextDatabaseName(string label)
    {
        var slug = new string(label.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        var suffix = $"_{Interlocked.Increment(ref _databaseCount)}";

        // Postgres caps identifiers at 63 bytes.
        var maxSlug = Math.Max(1, 63 - suffix.Length);
        if (slug.Length > maxSlug)
        {
            slug = slug[..maxSlug];
        }

        return slug + suffix;
    }

    /// <summary>Disposes the managed container, if one was started.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
