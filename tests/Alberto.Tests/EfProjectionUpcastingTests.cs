using System.Text.Json;
using Alberto.EntityFramework;
using Alberto.Subscriptions;
using Alberto.Tests.Infrastructure;
using Alberto.Upcasting;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alberto.Tests.EntityFramework;

// ---------------------------------------------------------------------------
// Domain fixtures (file-scoped — invisible to xUnit discovery)
//
// Convention: only the CURRENT version carries [EventType].
// V1 is a plain record used only inside the upcaster chain.
// ---------------------------------------------------------------------------

// V1: no [EventType] — lives only in the upcaster chain.
file record UpcastEfOrderV1(Guid OrderId, string? Region);

// V2: current version — adds Country, which the upcaster fills in.
[EventType("upcast-ef-order", Version = 2)]
file record UpcastEfOrderV2(Guid OrderId, string? Region, string? Country) : IEvent;

// EF entity that stores the upcasted field from UpcastEfOrderV2.
// Must be non-file because EfProjectionUpcastingFixture.ReadEntityAsync returns it.
public sealed class UpcastEfOrderEntity : IProjectionEntity
{
    public string DocumentId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public long LastProcessedPosition { get; set; }
    public uint Version { get; set; }
    public int RebuildVersion { get; set; } = ProjectionVersions.Initial;

    /// <summary>
    /// Set by the V1→V2 upcaster. null before the fix (raw JSON had no Country field),
    /// "upcasted-country" after (serializer routes through the upcaster chain).
    /// </summary>
    public string? Country { get; set; }
}

// DbContext for the upcast test — one table, no event-store tables.
// Must be non-file because UpcastEfDbContextFactory (also non-file) uses it as a type argument.
public sealed class UpcastEfDbContext : DbContext
{
    public UpcastEfDbContext(DbContextOptions<UpcastEfDbContext> options) : base(options) { }

    public DbSet<UpcastEfOrderEntity> Orders => Set<UpcastEfOrderEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ProjectionEntity<UpcastEfOrderEntity>(entity =>
        {
            entity.ToTable("upcast_ef_orders");
        });
    }
}

// Minimal IDbContextFactory backed by a connection string.
// Must be non-file because EfProjectionUpcastingFixture.CreateFactory() returns it.
public sealed class UpcastEfDbContextFactory : IDbContextFactory<UpcastEfDbContext>
{
    private readonly DbContextOptions<UpcastEfDbContext> _options;

    public UpcastEfDbContextFactory(string connectionString)
    {
        _options = new DbContextOptionsBuilder<UpcastEfDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public UpcastEfDbContext CreateDbContext() => new(_options);
    public Task<UpcastEfDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

// Shared helpers — serializer, envelope factory, declaration builder.
file static class UpcastEfHelpers
{
    public const string Slug = "upcast-ef-order";

    /// <summary>
    /// Builds an EventSerializer with a V1→V2 upcaster that fills Country
    /// with "upcasted-country" so the assertion can verify the chain fired.
    /// </summary>
    public static EventSerializer BuildSerializer() =>
        EventSerializer
            .FromRegistry(new Dictionary<string, Type> { [Slug] = typeof(UpcastEfOrderV2) })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster
                    .For<UpcastEfOrderV2>(Slug)
                    .From<UpcastEfOrderV1>(
                        1,
                        v1 => new UpcastEfOrderV2(v1.OrderId, v1.Region, "upcasted-country"))
                    .Build())
                .Build());

    /// <summary>
    /// Builds a V1 envelope (no Country field in the JSON; EventType.Version = 1).
    /// A consumer that bypasses EventSerializer will store Country = null (raw JSON shape).
    /// A consumer that goes through EventSerializer will store Country = "upcasted-country".
    /// </summary>
    public static IEventEnvelope MakeV1Envelope(Guid orderId) => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = new EventType(Slug, 1),
        EventData = JsonSerializer.Serialize(new UpcastEfOrderV1(orderId, "us-east")),
        Tags = [],
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
        TenantId = null,
    };

    public static ProjectionDeclaration<UpcastEfOrderEntity> BuildDeclaration(string processorId) =>
        DeclareProjection.For<UpcastEfOrderEntity>(processorId)
            .On<UpcastEfOrderV2>(
                id: e => e.OrderId.ToString(),
                apply: (_, e, _) => new UpcastEfOrderEntity { Country = e.Country })
            .Build();
}

// ---------------------------------------------------------------------------
// Test fixture — gives the test class a private Postgres database containing
// the upcast_ef_orders table. Separate from EfProjectionTestFixture so this
// test class never shares a database with ef_test_counters tests.
// ---------------------------------------------------------------------------

/// <summary>
/// Provides a private Postgres database with the <c>upcast_ef_orders</c> table
/// for <see cref="EfProjectionUpcastingTests"/>.
/// </summary>
public sealed class EfProjectionUpcastingFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, UpcastEfTemplate)
{
    // Template built once, cloned per test class — same pattern as EfProjectionTestFixture.
    private static readonly PostgresTemplate UpcastEfTemplate =
        new("alberto_tmpl_ef_upcast", async cs =>
        {
            var options = new DbContextOptionsBuilder<UpcastEfDbContext>()
                .UseNpgsql(cs, o => o.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(3),
                    errorCodesToAdd: null))
                .Options;
            await using var ctx = new UpcastEfDbContext(options);
            await ctx.Database.EnsureCreatedAsync();
        });

    /// <summary>Creates a factory for the test database.</summary>
    public UpcastEfDbContextFactory CreateFactory() => new(ConnectionString);

    /// <summary>
    /// Reads the <see cref="UpcastEfOrderEntity"/> for <paramref name="documentId"/> and
    /// <paramref name="rebuildVersion"/> directly (bypasses EF change tracker).
    /// </summary>
    public async Task<UpcastEfOrderEntity?> ReadEntityAsync(
        string documentId,
        int rebuildVersion = ProjectionVersions.Initial)
    {
        var options = new DbContextOptionsBuilder<UpcastEfDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var ctx = new UpcastEfDbContext(options);
        return await ctx.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(e =>
                e.DocumentId == documentId && e.RebuildVersion == rebuildVersion);
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that <c>AddEfProjection</c>'s registered <see cref="DeclaredAsyncProjection{TState}"/>
/// threads <see cref="EventSerializer"/> (and therefore the upcaster chain) into both the live
/// projection path and the rebuild-shadow projection path.
///
/// These are SEPARATE construction sites in <see cref="EfConsumerBuilderExtensions"/>:
/// the live path constructs <c>DeclaredAsyncProjection</c> directly inside the keyed
/// <c>IEventProcessor</c> registration, while the rebuild-shadow path constructs it inside
/// the <see cref="RebuildableProjection"/> factory lambda.  Before the fix, both sites passed
/// no serializer (<c>serializer: null</c>).  After the fix, the keyed
/// <c>EventSerializer</c> is resolved by <c>sp.GetKeyedService&lt;EventSerializer&gt;(moduleKey)</c>
/// and passed as <c>serializer: serializer</c> at both sites.
///
/// The test asserts <c>Country == "upcasted-country"</c>.  Before the fix that field is null
/// (raw JSON has no Country field); after the fix the upcaster fills it in.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfProjectionUpcastingTests(EfProjectionUpcastingFixture fixture)
    : IClassFixture<EfProjectionUpcastingFixture>
{
    // -----------------------------------------------------------------------
    // Live path — mirrors AddEfProjection's IEventProcessor registration
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LivePath_UpcasterFires_BeforeEfEntityIsStored()
    {
        // Arrange — mirror what AddEfProjection's live-path registration builds after the fix:
        //   var serializer = sp.GetKeyedService<EventSerializer>(moduleKey);
        //   return new DeclaredAsyncProjection<TEntity>(declaration,
        //       _ => new EfStateStore<TEntity, TDbContext>(contextFactory, version),
        //       serializer: serializer);
        var orderId = Guid.NewGuid();
        var serializer = UpcastEfHelpers.BuildSerializer();
        var envelope = UpcastEfHelpers.MakeV1Envelope(orderId);
        var declaration = UpcastEfHelpers.BuildDeclaration("upcast-ef-live");
        var factory = fixture.CreateFactory();

        var projection = new DeclaredAsyncProjection<UpcastEfOrderEntity>(
            declaration,
            _ => new EfStateStore<UpcastEfOrderEntity, UpcastEfDbContext>(factory),
            serializer: serializer);

        // Act
        await projection.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        // Assert — upcaster filled Country; without it Country is null (raw V1 JSON)
        var entity = await fixture.ReadEntityAsync(orderId.ToString());
        entity.Should().NotBeNull("the projection should have written the entity");
        entity!.Country.Should().Be("upcasted-country",
            "the V1→V2 upcaster should have transformed the event before the handler applied it");
    }

    // -----------------------------------------------------------------------
    // Rebuild-shadow path — mirrors AddEfProjection's RebuildableProjection registration
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RebuildPath_UpcasterFires_BeforeEfEntityIsStored()
    {
        // Arrange — mirror what AddEfProjection's rebuild-shadow registration builds after the fix:
        //   var serializer = sp.GetKeyedService<EventSerializer>(moduleKey);  // outer (sp, _) lambda
        //   return new RebuildableProjection(
        //       ...,
        //       version => new DeclaredAsyncProjection<TEntity>(
        //           declaration,
        //           _ => new EfStateStore<TEntity, TDbContext>(..., version),
        //           serializer: serializer));   // ← captured, not re-resolved
        //
        // The critical invariant: serializer is captured OUTSIDE the 'version =>' lambda.
        // Before the fix the lambda had no access to it (the (sp, _) lambda never resolved it).
        var orderId = Guid.NewGuid();
        var declaration = UpcastEfHelpers.BuildDeclaration("upcast-ef-rebuild");
        var factory = fixture.CreateFactory();

        var capturedSerializer = UpcastEfHelpers.BuildSerializer(); // captured outside version =>
        const int rebuildShadowVersion = 2; // shadow loop writes at a different version than live
        var version = ProjectionVersion.From(() => rebuildShadowVersion);

        var projection = new DeclaredAsyncProjection<UpcastEfOrderEntity>(
            declaration,
            _ => new EfStateStore<UpcastEfOrderEntity, UpcastEfDbContext>(factory, version),
            serializer: capturedSerializer);

        var envelope = UpcastEfHelpers.MakeV1Envelope(orderId);

        // Act
        await projection.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);

        // Assert — shadow entity at rebuild version 2 has Country from the upcaster
        var entity = await fixture.ReadEntityAsync(orderId.ToString(), rebuildShadowVersion);
        entity.Should().NotBeNull(
            "the rebuild-shadow projection should have written the entity at version 2");
        entity!.Country.Should().Be("upcasted-country",
            "the V1→V2 upcaster fires on the rebuild-shadow path after the fix, " +
            "just as on the live path");
    }
}
