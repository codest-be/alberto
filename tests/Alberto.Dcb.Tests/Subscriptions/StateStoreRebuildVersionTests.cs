using Alberto.Dcb.EntityFramework;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tests.EntityFramework;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Fixture running both the shipped single-tenant migrations (for
/// <see cref="PostgresStateStore{TState}"/>) and the EF test schema (for
/// <see cref="EfStateStore{TEntity,TDbContext}"/>) against one container, so both backends
/// can be held to the same version-isolation contract side by side.
/// </summary>
public sealed class StateStoreRebuildVersionFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // EF's EnsureCreated is a no-op once the database has any tables at all, so it has to
        // run before DbUp puts the alberto_* tables there.
        var optionsBuilder = new DbContextOptionsBuilder<EfTestDbContext>();
        optionsBuilder.UseNpgsql(ConnectionString);
        await using (var context = new EfTestDbContext(optionsBuilder.Options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        var migrationResult = PostgresMigrator.Migrate(ConnectionString, singleTenant: true);
        if (!migrationResult.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed: {migrationResult.Error?.Message}",
                migrationResult.Error);
        }

        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public IDbContextFactory<EfTestDbContext> ContextFactory()
        => new ControlledDbContextFactory(ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// Both state stores must keep rebuild versions apart: a shadow loop replaying history into
/// version N+1 has to be invisible to the live projection reading version N, in the same
/// table, at the same time. Everything else about a hot rebuild rests on that.
///
/// The version is resolved per operation rather than captured at construction, so each test
/// drives it through a mutable selector — that models a promotion landing underneath a
/// long-lived store, which is exactly when getting this wrong would corrupt a projection.
/// </summary>
public sealed class StateStoreRebuildVersionTests(StateStoreRebuildVersionFixture fixture)
    : IClassFixture<StateStoreRebuildVersionFixture>
{
    private sealed record Counter(string Id, int Value);

    private static string NewProjectionType() => $"type-{Guid.NewGuid():N}";

    // ---------------------------------------------------------------- Postgres

    private PostgresStateStore<Counter> PostgresStore(string projectionType, Func<int> version)
        => new(fixture.DataSource, projectionType, schema: null, rebuildVersion: version);

    [Fact]
    public async Task PostgresStateStore_WritesAtOneVersionAreInvisibleAtAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = NewProjectionType();

        var live = PostgresStore(projectionType, () => 1);
        var shadow = PostgresStore(projectionType, () => 2);

        await live.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["doc-1"] = new("doc-1", 10) }, [], ct: ct);
        await shadow.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["doc-1"] = new("doc-1", 99) }, [], ct: ct);

        var liveState = await live.LoadManyAsync(["doc-1"], ct: ct);
        var shadowState = await shadow.LoadManyAsync(["doc-1"], ct: ct);

        liveState["doc-1"].Value.Should().Be(
            10, "the shadow rebuild must not overwrite what readers are looking at");
        shadowState["doc-1"].Value.Should().Be(99);
    }

    [Fact]
    public async Task PostgresStateStore_DeletesOnlyAffectTheVersionBeingWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = NewProjectionType();

        var live = PostgresStore(projectionType, () => 1);
        var shadow = PostgresStore(projectionType, () => 2);

        await live.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["doc-1"] = new("doc-1", 10) }, [], ct: ct);
        await shadow.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["doc-1"] = new("doc-1", 99) }, [], ct: ct);

        // A rebuild that replays a delete must not delete the live row.
        await shadow.ApplyChangesAsync(new Dictionary<string, Counter>(), ["doc-1"], ct: ct);

        (await live.LoadManyAsync(["doc-1"], ct: ct)).Should().ContainKey("doc-1");
        (await shadow.LoadManyAsync(["doc-1"], ct: ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task PostgresStateStore_FollowsThePromotionOfItsVersionMidLife()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = NewProjectionType();

        var activeVersion = 1;
        // One long-lived store, as the live projection has. This is the store a promotion
        // moves underneath.
        var reader = PostgresStore(projectionType, () => activeVersion);

        await PostgresStore(projectionType, () => 1).ApplyChangesAsync(
            new Dictionary<string, Counter> { ["doc-1"] = new("doc-1", 10) }, [], ct: ct);
        await PostgresStore(projectionType, () => 2).ApplyChangesAsync(
            new Dictionary<string, Counter> { ["doc-1"] = new("doc-1", 99) }, [], ct: ct);

        (await reader.LoadManyAsync(["doc-1"], ct: ct))["doc-1"].Value.Should().Be(10);

        activeVersion = 2;   // promotion

        (await reader.LoadManyAsync(["doc-1"], ct: ct))["doc-1"].Value.Should().Be(
            99, "resolving the version per operation is what makes a promotion take effect " +
                "without rebuilding every state store");
    }

    [Fact]
    public async Task PostgresStateStore_ListRecentIsScopedToTheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = NewProjectionType();

        var live = PostgresStore(projectionType, () => 1);
        var shadow = PostgresStore(projectionType, () => 2);

        await live.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["doc-1"] = new("doc-1", 10) }, [], ct: ct);
        await shadow.ApplyChangesAsync(
            new Dictionary<string, Counter>
            {
                ["doc-1"] = new("doc-1", 99),
                ["doc-2"] = new("doc-2", 98),
            }, [], ct: ct);

        (await live.ListRecentAsync(50, ct)).Should().HaveCount(
            1, "a reader must not see the half-finished rebuild alongside the live rows");
        (await shadow.ListRecentAsync(50, ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task PostgresStateStore_DefaultsToVersionOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = NewProjectionType();

        // No selector at all — the shape almost every projection uses.
        var plain = new PostgresStateStore<Counter>(fixture.DataSource, projectionType);
        await plain.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["doc-1"] = new("doc-1", 7) }, [], ct: ct);

        var atVersionOne = PostgresStore(projectionType, () => 1);
        (await atVersionOne.LoadManyAsync(["doc-1"], ct: ct))["doc-1"].Value.Should().Be(7);
    }

    // ---------------------------------------------------------------------- EF

    private EfStateStore<CounterEntity, EfTestDbContext> EfStore(Func<int> version)
        => new(fixture.ContextFactory(), version);

    private static CounterEntity Entity(string docId, int counter)
        => new() { DocumentId = docId, Counter = counter };

    /// <summary>Document ids are per-test because the EF entities share one table.</summary>
    private static string NewDocId() => $"doc-{Guid.NewGuid():N}";

    [Fact]
    public async Task EfStateStore_WritesAtOneVersionAreInvisibleAtAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();

        var live = EfStore(() => 1);
        var shadow = EfStore(() => 2);

        await live.ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 10) }, [], ct: ct);
        await shadow.ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 99) }, [], ct: ct);

        var liveState = await live.LoadManyAsync([docId], ct: ct);
        var shadowState = await shadow.LoadManyAsync([docId], ct: ct);

        liveState[docId].Counter.Should().Be(
            10, "without the version in the key the rebuild would have overwritten this row");
        shadowState[docId].Counter.Should().Be(99);
    }

    [Fact]
    public async Task EfStateStore_StampsTheVersionItWroteAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();

        // The entity carries the default of 1; the store must overwrite it rather than
        // trusting the caller to have set it.
        await EfStore(() => 3).ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 42) }, [], ct: ct);

        (await EfStore(() => 3).LoadManyAsync([docId], ct: ct))[docId]
            .RebuildVersion.Should().Be(3);
        (await EfStore(() => 1).LoadManyAsync([docId], ct: ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task EfStateStore_DeletesOnlyAffectTheVersionBeingWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();

        var live = EfStore(() => 1);
        var shadow = EfStore(() => 2);

        await live.ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 10) }, [], ct: ct);
        await shadow.ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 99) }, [], ct: ct);

        await shadow.ApplyChangesAsync(new Dictionary<string, CounterEntity>(), [docId], ct: ct);

        (await live.LoadManyAsync([docId], ct: ct)).Should().ContainKey(docId);
        (await shadow.LoadManyAsync([docId], ct: ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task EfStateStore_FollowsThePromotionOfItsVersionMidLife()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();

        var activeVersion = 1;
        var reader = EfStore(() => activeVersion);

        await EfStore(() => 1).ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 10) }, [], ct: ct);
        await EfStore(() => 2).ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 99) }, [], ct: ct);

        (await reader.LoadManyAsync([docId], ct: ct))[docId].Counter.Should().Be(10);

        activeVersion = 2;   // promotion

        (await reader.LoadManyAsync([docId], ct: ct))[docId].Counter.Should().Be(99);
    }

    [Fact]
    public async Task EfProjectionStateClearer_DropsExactlyOneVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();

        await EfStore(() => 1).ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 10) }, [], ct: ct);
        await EfStore(() => 2).ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 99) }, [], ct: ct);

        IProjectionStateClearer clearer =
            new EfProjectionStateClearer<CounterEntity, EfTestDbContext>(
                fixture.ContextFactory(), "counter-projection");

        await clearer.ClearVersionAsync(2, ct);

        (await EfStore(() => 1).LoadManyAsync([docId], ct: ct)).Should().ContainKey(
            docId, "clearing the abandoned rebuild must leave the live projection standing");
        (await EfStore(() => 2).LoadManyAsync([docId], ct: ct)).Should().BeEmpty();

        // Re-running is a no-op, which is what makes it safe after a coordinator crash.
        await clearer.ClearVersionAsync(2, ct);
        (await EfStore(() => 1).LoadManyAsync([docId], ct: ct)).Should().ContainKey(docId);
    }

    [Fact]
    public async Task EfStateStore_DefaultsToVersionOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();

        var plain = new EfStateStore<CounterEntity, EfTestDbContext>(fixture.ContextFactory());
        await plain.ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 7) }, [], ct: ct);

        (await EfStore(() => 1).LoadManyAsync([docId], ct: ct))[docId].Counter.Should().Be(7);
    }
}
