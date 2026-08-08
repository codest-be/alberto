using Alberto.EntityFramework;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Tests.EntityFramework;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Fixture running both the shipped single-tenant migrations (for
/// <see cref="PostgresStateStore{TState}"/>) and the EF test schema (for
/// <see cref="EfStateStore{TEntity,TDbContext}"/>) against one database, so both backends
/// can be held to the same version-isolation contract side by side.
/// </summary>
public sealed class StateStoreRebuildVersionFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.EntityFrameworkThenSingleTenant)
{
    public IDbContextFactory<EfTestDbContext> ContextFactory()
        => new ControlledDbContextFactory(ConnectionString);
}

/// <summary>
/// Adapter-specific facts that go beyond the <see cref="Alberto.Testing.Xunit.StateStoreSpecification{TState}"/>
/// contract — either because they test behaviour that is not part of the
/// <see cref="IStateStore{TState}"/> interface (<c>ListRecentAsync</c>, entity stamping)
/// or because they exercise the EF retry/conflict machinery that is an implementation
/// detail of <see cref="EfStateStore{TEntity,TDbContext}"/>.
///
/// The version-isolation contract facts (writes invisible at another version, deletes
/// scoped to their version, promotion following, default-is-1) have been absorbed into
/// <see cref="Alberto.Testing.Xunit.StateStoreSpecification{TState}"/> and are exercised against all three
/// adapters there.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StateStoreRebuildVersionTests(StateStoreRebuildVersionFixture fixture)
    : IClassFixture<StateStoreRebuildVersionFixture>
{
    private sealed record Counter(string Id, int Value);

    private static string NewProjectionType() => $"type-{Guid.NewGuid():N}";

    // ---------------------------------------------------------------- Postgres

    private PostgresStateStore<Counter> PostgresStore(string projectionType, ProjectionVersion version)
        => new(fixture.DataSource, projectionType, schema: null, rebuildVersion: version);

    /// <summary>
    /// <see cref="PostgresStateStore{TState}.ListRecentAsync"/> is not part of
    /// <see cref="IStateStore{TState}"/>; this fact belongs here rather than in the spec.
    /// </summary>
    [Fact]
    public async Task PostgresStateStore_ListRecentIsScopedToTheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = NewProjectionType();

        var live = PostgresStore(projectionType, ProjectionVersion.From(() => 1));
        var shadow = PostgresStore(projectionType, ProjectionVersion.From(() => 2));

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

    // ---------------------------------------------------------------------- EF

    private EfStateStore<CounterEntity, EfTestDbContext> EfStore(ProjectionVersion version)
        => new(fixture.ContextFactory(), version);

    private static CounterEntity Entity(string docId, int counter)
        => new() { DocumentId = docId, Counter = counter };

    /// <summary>Document ids are per-test because the EF entities share one table.</summary>
    private static string NewDocId() => $"doc-{Guid.NewGuid():N}";

    /// <summary>
    /// The store must stamp the entity's <see cref="IProjectionEntity.RebuildVersion"/>
    /// from its own selector rather than trusting whatever value the caller set on the
    /// entity. This is an EF-specific invariant; the JSONB adapters store the document
    /// as an opaque blob and carry the version in a separate column.
    /// </summary>
    [Fact]
    public async Task EfStateStore_StampsTheVersionItWroteAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();

        // The entity carries the default of 1; the store must overwrite it rather than
        // trusting the caller to have set it.
        await EfStore(ProjectionVersion.From(() => 3)).ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 42) }, [], ct: ct);

        (await EfStore(ProjectionVersion.From(() => 3)).LoadManyAsync([docId], ct: ct))[docId]
            .RebuildVersion.Should().Be(3);
        (await EfStore(ProjectionVersion.From(() => 1)).LoadManyAsync([docId], ct: ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task EfProjectionStateClearer_DropsExactlyOneVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();

        await EfStore(ProjectionVersion.From(() => 1)).ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 10) }, [], ct: ct);
        await EfStore(ProjectionVersion.From(() => 2)).ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 99) }, [], ct: ct);

        IProjectionStateClearer clearer =
            new EfProjectionStateClearer<CounterEntity, EfTestDbContext>(
                fixture.ContextFactory(), "counter-projection");

        await clearer.ClearVersionAsync(2, ct);

        (await EfStore(ProjectionVersion.From(() => 1)).LoadManyAsync([docId], ct: ct)).Should().ContainKey(
            docId, "clearing the abandoned rebuild must leave the live projection standing");
        (await EfStore(ProjectionVersion.From(() => 2)).LoadManyAsync([docId], ct: ct)).Should().BeEmpty();

        // Re-running is a no-op, which is what makes it safe after a coordinator crash.
        await clearer.ClearVersionAsync(2, ct);
        (await EfStore(ProjectionVersion.From(() => 1)).LoadManyAsync([docId], ct: ct)).Should().ContainKey(docId);
    }

    [Fact]
    public async Task EfStateStore_RetriesTheCompleteAtomicBatch_AndResolvesVersionOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var updatedId = NewDocId();
        var deletedId = NewDocId();
        await EfStore(ProjectionVersion.From(() => 1)).ApplyChangesAsync(
            new Dictionary<string, CounterEntity>
            {
                [updatedId] = Entity(updatedId, 10),
                [deletedId] = Entity(deletedId, 20),
            },
            [],
            ct);

        var nestedUniqueViolation = new DbUpdateException(
            "save failed",
            new InvalidOperationException(
                "provider wrapper",
                new FakeSqlStateDbException("23505", "duplicate key")));
        var factory = new ControlledDbContextFactory(fixture.ConnectionString);
        factory.EnqueueException(nestedUniqueViolation);
        factory.EnqueueSuccess();
        var versionResolutionCount = 0;
        var store = new EfStateStore<CounterEntity, EfTestDbContext>(
            factory,
            ProjectionVersion.From(() =>
            {
                versionResolutionCount++;
                return 1;
            }));

        await store.ApplyChangesAsync(
            new Dictionary<string, CounterEntity>
            {
                [updatedId] = Entity(updatedId, 99),
            },
            [deletedId],
            ct);

        var state = await EfStore(ProjectionVersion.From(() => 1)).LoadManyAsync([updatedId, deletedId], ct);
        state[updatedId].Counter.Should().Be(99);
        state.Should().NotContainKey(deletedId, "deletes are part of every atomic retry attempt");
        versionResolutionCount.Should().Be(
            1,
            "one ApplyChanges operation cannot cross rebuild versions between attempts");
    }

    [Fact]
    public async Task EfStateStore_RetriesOptimisticConcurrencyThroughTheSameMutationPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();
        var factory = new ControlledDbContextFactory(fixture.ConnectionString);
        factory.EnqueueException(new DbUpdateConcurrencyException("stale row", []));
        factory.EnqueueSuccess();
        var store = new EfStateStore<CounterEntity, EfTestDbContext>(factory);

        await store.ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 42) },
            [],
            ct);

        (await store.LoadManyAsync([docId], ct))[docId].Counter.Should().Be(42);
    }

    [Fact]
    public async Task EfStateStore_ExhaustedConflict_ThrowsAndNeverReportsPartialSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = NewDocId();
        var permanentConflict = new DbUpdateException(
            "save failed",
            new FakeSqlStateDbException("23505", "duplicate key"));
        var factory = new ControlledDbContextFactory(fixture.ConnectionString)
        {
            PermanentInterceptor = (_, _) => throw permanentConflict,
        };
        var store = new EfStateStore<CounterEntity, EfTestDbContext>(factory);

        var act = () => store.ApplyChangesAsync(
            new Dictionary<string, CounterEntity> { [docId] = Entity(docId, 42) },
            [],
            ct);

        var thrown = await act.Should().ThrowAsync<ConcurrencyConflictException>();
        thrown.Which.DocumentId.Should().Be(docId);
        thrown.Which.InnerException.Should().BeSameAs(permanentConflict);
        (await EfStore(ProjectionVersion.From(() => 1)).LoadManyAsync([docId], ct)).Should().BeEmpty();
    }
}
