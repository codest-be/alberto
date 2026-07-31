using System.Data.Common;
using System.Text.Json;
using Alberto.EntityFramework;
using Alberto.Subscriptions;
using Alberto.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alberto.Tests.EntityFramework;

// ---------------------------------------------------------------------------
// Shared domain model used by TEST-1 and TEST-10
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal event for EF projection tests.
/// </summary>
[EventType("counter-incremented")]
public record CounterIncremented(string EntityId, int Amount) : IEvent;

/// <summary>
/// Entity tracked by EF that also implements <see cref="IProjectionEntity"/>.
/// </summary>
public class CounterEntity : IProjectionEntity
{
    public string DocumentId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public long LastProcessedPosition { get; set; }
    public uint Version { get; set; }
    public int RebuildVersion { get; set; } = 1;

    /// <summary>Running counter value, updated by each <see cref="CounterIncremented"/> event.</summary>
    public int Counter { get; set; }
}

// ---------------------------------------------------------------------------
// Test DbContext with injectable save interception
// ---------------------------------------------------------------------------

/// <summary>
/// EF DbContext used by EF projection tests.
/// Exposes a <see cref="SaveInterceptor"/> that overrides <see cref="SaveChangesAsync"/>
/// so tests can inject faults without replacing the full database provider.
/// </summary>
public sealed class EfTestDbContext : DbContext
{
    /// <summary>
    /// When set, called instead of the real <see cref="DbContext.SaveChangesAsync"/>.
    /// The delegate receives a callback to the real base implementation so that
    /// "fail N times, then succeed" test patterns remain easy to express.
    /// </summary>
    public Func<Func<CancellationToken, Task<int>>, CancellationToken, Task<int>>? SaveInterceptor { get; set; }

    private readonly Func<CancellationToken, Task<int>> _baseSave;

    public EfTestDbContext(DbContextOptions<EfTestDbContext> options) : base(options)
    {
        _baseSave = ct => base.SaveChangesAsync(ct);
    }

    public DbSet<CounterEntity> Counters => Set<CounterEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ProjectionEntity applies the (DocumentId, RebuildVersion) key that EfStateStore's
        // version filtering assumes, so these tests exercise the same model configuration
        // consumers are told to use.
        modelBuilder.ProjectionEntity<CounterEntity>(entity =>
        {
            entity.ToTable("ef_test_counters");
            entity.Property(e => e.DocumentId).HasMaxLength(256);
            entity.Property(e => e.Counter);
            entity.Property(e => e.LastProcessedPosition);
            entity.Property(e => e.UpdatedAt);
            // Omit xmin-based Version for simplicity; tests inject concurrency exceptions.
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (SaveInterceptor is not null)
            return SaveInterceptor(_baseSave, cancellationToken);

        return _baseSave(cancellationToken);
    }
}

// ---------------------------------------------------------------------------
// Controllable DbContext factory
// ---------------------------------------------------------------------------

/// <summary>
/// <see cref="IDbContextFactory{EfTestDbContext}"/> whose produced contexts have their
/// <c>SaveChangesAsync</c> behaviour controlled by a pre-queued sequence of interceptors.
/// Once the queue is exhausted, contexts fall through to the real save.
/// A <see cref="PermanentInterceptor"/> can be set to make every context fail indefinitely.
/// </summary>
public sealed class ControlledDbContextFactory : IDbContextFactory<EfTestDbContext>
{
    private readonly DbContextOptions<EfTestDbContext> _options;
    private readonly Queue<Func<Func<CancellationToken, Task<int>>, CancellationToken, Task<int>>> _queue = new();

    /// <summary>
    /// When non-null, applied to every context regardless of the queue state.
    /// Useful for exhaustion tests that need all five retry attempts to fail.
    /// </summary>
    public Func<Func<CancellationToken, Task<int>>, CancellationToken, Task<int>>? PermanentInterceptor { get; set; }

    public ControlledDbContextFactory(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<EfTestDbContext>();
        builder.UseNpgsql(connectionString);
        _options = builder.Options;
    }

    /// <summary>Enqueues a save call that throws <paramref name="ex"/>.</summary>
    public void EnqueueException(Exception ex)
        => _queue.Enqueue((_, _) => throw ex);

    /// <summary>Enqueues a save call that delegates to the real <c>SaveChangesAsync</c>.</summary>
    public void EnqueueSuccess()
        => _queue.Enqueue((baseSave, ct) => baseSave(ct));

    public EfTestDbContext CreateDbContext()
    {
        var context = new EfTestDbContext(_options);

        if (_queue.TryDequeue(out var interceptor))
            context.SaveInterceptor = interceptor;
        else if (PermanentInterceptor is not null)
            context.SaveInterceptor = PermanentInterceptor;

        return context;
    }

    public Task<EfTestDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

// ---------------------------------------------------------------------------
// Helpers shared across test classes
// ---------------------------------------------------------------------------

/// <summary>
/// Fake <see cref="DbException"/> subclass that carries a synthetic <see cref="SqlState"/>.
/// Used to build <see cref="DbUpdateException"/> instances that the retry guard recognises
/// as unique-constraint violations (SQLSTATE 23505) without needing a real database error.
/// </summary>
public sealed class FakeSqlStateDbException : DbException
{
    public FakeSqlStateDbException(string sqlState, string message = "fake db error")
        : base(message)
    {
        SqlState = sqlState;
    }

    public override string? SqlState { get; }
}

/// <summary>
/// Factory helpers for the event envelopes used across EF projection tests.
/// </summary>
public static class EfTestEnvelopes
{
    public static IEventEnvelope ForCounterIncremented(
        string entityId, int amount, long position) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            GlobalPosition = position,
            EventType = new EventType("counter-incremented"),
            Tags = [],
            EventData = JsonSerializer.Serialize(new CounterIncremented(entityId, amount)),
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };
}

// ---------------------------------------------------------------------------
// xUnit fixture — a private database on the shared cluster
// ---------------------------------------------------------------------------

/// <summary>
/// Gives each EF test class its own database carrying the EF test entities.
/// </summary>
/// <remarks>
/// The schema comes from <see cref="DbContext.Database.EnsureCreatedAsync"/> rather than
/// Aspire/DbUp, because the EF test entities live in a table Alberto's migrations do not
/// manage. Both this and the container it used to own are per test class — sharing happens
/// at the container, not the database.
/// </remarks>
public sealed class EfProjectionTestFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.EntityFramework)
{
    /// <summary>
    /// Builds a <see cref="ControlledDbContextFactory"/> pointing at this class's database.
    /// Each test should create its own factory so that queued interceptors are independent.
    /// </summary>
    public ControlledDbContextFactory CreateFactory() => new(ConnectionString);

    /// <summary>
    /// Builds a standard <see cref="ProjectionDeclaration{CounterEntity}"/> that projects
    /// <see cref="CounterIncremented"/> events onto <see cref="CounterEntity"/> rows.
    /// </summary>
    public static ProjectionDeclaration<CounterEntity> BuildDeclaration(string processorId = "counter-projection") =>
        DeclareProjection.For<CounterEntity>(processorId)
            .On<CounterIncremented>(
                id: e => e.EntityId,
                // CounterEntity is a class, not a record — return a new instance.
                // DocumentId, UpdatedAt, and LastProcessedPosition are set by the infrastructure.
                apply: (state, e, _) => new CounterEntity { Counter = state.Counter + e.Amount })
            .Build();

    /// <summary>
    /// Reads the current <see cref="CounterEntity"/> for <paramref name="entityId"/>
    /// directly from the database using a fresh context (bypasses EF change tracker).
    /// </summary>
    public async Task<CounterEntity?> ReadEntityAsync(string entityId)
    {
        await using var context = NewContext();
        return await context.Counters
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.DocumentId == entityId);
    }

    /// <summary>
    /// Reads every stored row for <paramref name="entityId"/>, keyed by
    /// <see cref="IProjectionEntity.RebuildVersion"/>. Rebuild tests need to see the versions
    /// the live read path deliberately filters out.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, CounterEntity>> ReadVersionsAsync(string entityId)
    {
        await using var context = NewContext();
        return await context.Counters
            .AsNoTracking()
            .Where(e => e.DocumentId == entityId)
            .ToDictionaryAsync(e => e.RebuildVersion);
    }

    /// <summary>
    /// Inserts a row directly, bypassing the projection, so a test can set up a table state
    /// that only a shadow rebuild loop would otherwise produce.
    /// </summary>
    public async Task SeedAsync(CounterEntity entity)
    {
        await using var context = NewContext();
        context.Counters.Add(entity);
        await context.SaveChangesAsync();
    }

    private EfTestDbContext NewContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EfTestDbContext>();
        optionsBuilder.UseNpgsql(ConnectionString);
        return new EfTestDbContext(optionsBuilder.Options);
    }
}
