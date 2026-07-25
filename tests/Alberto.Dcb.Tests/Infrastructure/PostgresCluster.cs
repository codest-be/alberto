using System.Collections.Concurrent;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Tests.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Alberto.Dcb.Tests.Infrastructure.PostgresCluster))]

namespace Alberto.Dcb.Tests.Infrastructure;

/// <summary>
/// A named template database: created once, given its schema once, then cloned per test class.
/// </summary>
/// <param name="Name">Database name of the template, and the key it is memoised under.</param>
/// <param name="BuildAsync">
/// Applies the schema. Receives a connection string with pooling disabled — see
/// <see cref="PostgresCluster.BuildTemplateAsync"/> for why that is not optional.
/// </param>
public sealed record PostgresTemplate(string Name, Func<string, Task> BuildAsync);

/// <summary>
/// The distinct database setups the suite needs. Four of them, where the fixtures that
/// preceded this class expressed the same four setups nine times over.
/// </summary>
public static class PostgresTemplates
{
    /// <summary>Event store schema without tenant_id columns on the events tables.</summary>
    public static readonly PostgresTemplate SingleTenant =
        new("alberto_tmpl_single_tenant", cs => RunDbUpAsync(cs, singleTenant: true));

    /// <summary>Event store schema with tenant_id columns.</summary>
    public static readonly PostgresTemplate MultiTenant =
        new("alberto_tmpl_multi_tenant", cs => RunDbUpAsync(cs, singleTenant: false));

    /// <summary>
    /// A database with nothing in it, for schemas that are not the event store's — the tenant
    /// shard catalog, whose own migrator is what the test is there to exercise.
    /// </summary>
    public static readonly PostgresTemplate Empty =
        new("alberto_tmpl_empty", _ => Task.CompletedTask);

    /// <summary>
    /// The EF test entities alone. EnsureCreated is sufficient because those entities live
    /// in a table that Alberto's migrations do not manage.
    /// </summary>
    public static readonly PostgresTemplate EntityFramework =
        new("alberto_tmpl_ef", EnsureEfCreatedAsync);

    /// <summary>
    /// The EF test entities and the single-tenant event store together. EF's EnsureCreated is
    /// a no-op once the database has any tables at all, so it has to run before DbUp puts the
    /// alberto_* tables there. The ordering is load-bearing; do not swap these two lines.
    /// </summary>
    public static readonly PostgresTemplate EntityFrameworkThenSingleTenant =
        new("alberto_tmpl_ef_single_tenant", async cs =>
        {
            await EnsureEfCreatedAsync(cs);
            await RunDbUpAsync(cs, singleTenant: true);
        });

    private static Task RunDbUpAsync(string connectionString, bool singleTenant)
    {
        var result = PostgresMigrator.Migrate(connectionString, singleTenant: singleTenant);
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed: {result.Error?.Message}",
                result.Error);
        }

        return Task.CompletedTask;
    }

    private static async Task EnsureEfCreatedAsync(string connectionString)
    {
        // The retry policy is inherited from the per-class fixtures this replaced, where it was
        // added to stop a freshly started container from failing EnsureCreated. Building the
        // template is still the first thing to touch a just-started postmaster, so it still applies.
        var options = new DbContextOptionsBuilder<EfTestDbContext>()
            .UseNpgsql(connectionString, o => o.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(3),
                errorCodesToAdd: null))
            .Options;
        await using var context = new EfTestDbContext(options);
        await context.Database.EnsureCreatedAsync();
    }
}

/// <summary>
/// One PostgreSQL container for the whole test assembly, handing every test class a private
/// database cloned from a template that was migrated once.
/// </summary>
/// <remarks>
/// The suite used to start about twenty containers, each running its own postmaster and paying
/// a full DbUp migration. This keeps the isolation those containers bought — a test class still
/// gets a database nobody else touches — while paying for one postmaster and four migrations.
/// <para>
/// Nothing here shares a database between test classes, so class-level parallelism is exactly
/// what it was and no test needs auditing for cross-class interference.
/// </para>
/// </remarks>
public sealed class PostgresCluster : IAsyncLifetime
{
    /// <summary>
    /// One postmaster now serves every test class, so max_connections is shared rather than
    /// per-container. Raised well past the default 100, with each fixture's pool capped to match.
    /// </summary>
    private const int MaxConnections = 200;

    /// <summary>Per-fixture pool ceiling, so sixteen fixtures cannot exhaust the server.</summary>
    private const int MaxPoolSizePerFixture = 10;

    /// <summary>Postgres caps identifiers at 63 bytes.</summary>
    private const int MaxIdentifierLength = 63;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithCommand("-c", $"max_connections={MaxConnections}")
        .Build();

    private readonly ConcurrentDictionary<string, Lazy<Task>> _templates = new();

    private string _adminConnectionString = string.Empty;
    private int _databaseCount;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
    }

    /// <summary>
    /// Creates a fresh database from <paramref name="template"/> and returns a connection string
    /// for it. The template is built on first use and reused thereafter.
    /// </summary>
    /// <param name="label">Names the database in server-side views; typically the fixture type.</param>
    public async Task<string> CloneAsync(
        PostgresTemplate template,
        string label,
        CancellationToken ct = default)
    {
        await EnsureTemplateAsync(template);

        var database = NextDatabaseName(label);
        await ExecuteAsync(
            _adminConnectionString,
            $"""CREATE DATABASE "{database}" TEMPLATE "{template.Name}";""",
            ct);

        return ConnectionStringFor(database, b => b.MaxPoolSize = MaxPoolSizePerFixture);
    }

    /// <remarks>
    /// The <see cref="Lazy{T}"/> holds the build <em>task</em>, so a migration that fails is
    /// cached and rethrown to every waiter. Nine fixtures queued behind one broken template
    /// surface one error rather than nine racing retries.
    /// </remarks>
    private Task EnsureTemplateAsync(PostgresTemplate template) =>
        _templates.GetOrAdd(
            template.Name,
            _ => new Lazy<Task>(() => BuildTemplateAsync(template))).Value;

    private async Task BuildTemplateAsync(PostgresTemplate template)
    {
        await ExecuteAsync(_adminConnectionString, $"""CREATE DATABASE "{template.Name}";""");

        // Pooling is disabled deliberately, and this is the sharp edge of the whole design:
        // closing an Npgsql connection returns it to the pool while leaving it physically
        // open, and CREATE DATABASE ... TEMPLATE refuses to run while any session is connected
        // to the source ("source database is being accessed by other users"). A pooled
        // connection left over from the migration would break every clone that followed.
        await template.BuildAsync(ConnectionStringFor(template.Name, b => b.Pooling = false));
    }

    private string ConnectionStringFor(
        string database,
        Action<NpgsqlConnectionStringBuilder>? configure = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = database,
        };
        configure?.Invoke(builder);
        return builder.ConnectionString;
    }

    /// <summary>
    /// Slugs <paramref name="label"/> into a legal identifier and appends a counter, so two
    /// fixtures sharing a label still get separate databases.
    /// </summary>
    private string NextDatabaseName(string label)
    {
        var suffix = $"_{Interlocked.Increment(ref _databaseCount)}";
        var slug = new string(label.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var room = MaxIdentifierLength - suffix.Length;
        if (slug.Length > room)
        {
            slug = slug[..room];
        }

        return slug + suffix;
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    // The cloned databases are not dropped: the container is torn down at the end of the run
    // and takes all of them with it.
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// Base for fixtures that need a private database on the shared <see cref="PostgresCluster"/>.
/// </summary>
/// <remarks>
/// Derived fixtures keep the surface their test classes already use — <see cref="DataSource"/>,
/// <see cref="ConnectionString"/> and their own helpers — so moving off per-class containers
/// changed no test class.
/// </remarks>
public abstract class PostgresDatabaseFixture(PostgresCluster cluster, PostgresTemplate template)
    : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await cluster.CloneAsync(template, GetType().Name);
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await OnInitializedAsync();
    }

    /// <summary>Runs once the private database and <see cref="DataSource"/> are ready.</summary>
    protected virtual ValueTask OnInitializedAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // InitializeAsync may have failed before this was assigned — a template migration
        // that threw, say. Returning early keeps that error visible instead of burying it
        // under a NullReferenceException from teardown.
        if (DataSource is null)
        {
            return;
        }

        await OnDisposingAsync();
        await DataSource.DisposeAsync();
    }

    /// <summary>Runs before <see cref="DataSource"/> is disposed.</summary>
    protected virtual ValueTask OnDisposingAsync() => ValueTask.CompletedTask;
}
