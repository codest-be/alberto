using Alberto.EntityFramework;
using Alberto.EntityFramework.Inline;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Tests.Subscriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// The model half of <see cref="ProjectionQueryFilters.ActiveVersion"/>: what SQL a plain read
/// turns into. No database needed, because <c>ToQueryString</c> never opens a connection.
/// </summary>
public sealed class EfActiveVersionFilterModelTests
{
    private static EfTestDbContext NewContext() =>
        new(new DbContextOptionsBuilder<EfTestDbContext>()
            .UseNpgsql("Host=unused")
            .Options);

    [Fact]
    public void Plain_read_resolves_the_active_version_in_sql()
    {
        using var context = NewContext();

        var sql = context.ActiveCounters.ToQueryString();

        sql.Should().Contain("alberto_projection_rebuild_meta",
            "the active version must be read per query, not cached on a pooled context");
        sql.Should().Contain("active_version");
    }

    [Fact]
    public void Ignoring_the_named_filter_drops_the_subquery()
    {
        using var context = NewContext();

        context.ActiveCounters
            .IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])
            .ToQueryString()
            .Should().NotContain("alberto_projection_rebuild_meta");
    }

    [Fact]
    public void Overload_without_a_processor_id_adds_no_filter()
    {
        using var context = NewContext();

        context.Counters.ToQueryString().Should().NotContain("alberto_projection_rebuild_meta");
    }

    [Fact]
    public void Rebuild_meta_table_is_left_to_alberto_migrations()
    {
        using var context = NewContext();

        context.Database.GenerateCreateScript().Should().NotContain("alberto_projection_rebuild_meta");
    }
}

/// <summary>
/// The runtime half of <see cref="ProjectionQueryFilters.ActiveVersion"/>, against a database
/// holding both the EF test tables and the event store schema, with the rebuild state driven
/// through <see cref="PostgresProjectionRebuildStore"/> so its constraints hold.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfActiveVersionFilterTests(StateStoreRebuildVersionFixture fixture)
    : IClassFixture<StateStoreRebuildVersionFixture>
{
    private const string ProcessorId = ActiveCounterEntity.ProcessorId;

    // One processor id for the whole class (the filter binds it in the model), so the tests
    // run one at a time against the one meta row and each starts from a clean slate.
    private static readonly SemaphoreSlim Serial = new(1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Plain_reads_follow_the_active_version_through_a_rebuild()
    {
        await using var _ = await IsolatedAsync();
        var docId = NewDocId();

        await Store(1).ApplyChangesAsync(Upsert(docId, 10), [], Ct);
        await StartRebuildAsync();
        await Store(2).ApplyChangesAsync(Upsert(docId, 20), [], Ct);

        (await PlainReadAsync(docId)).Should().Equal([10],
            "mid-rebuild, the shadow row must stay invisible to application reads");

        await PromoteAsync();

        (await PlainReadAsync(docId)).Should().Equal([20],
            "the very next query after the flip reads the new version, no refresh interval");
        (await UnfilteredReadAsync(docId)).Should().BeEquivalentTo([10, 20],
            "opting out of the named filter shows every version");
    }

    [Fact]
    public async Task Never_rebuilt_processor_reads_the_initial_version()
    {
        await using var _ = await IsolatedAsync();
        var docId = NewDocId();

        await Store(1).ApplyChangesAsync(Upsert(docId, 5), [], Ct);

        (await PlainReadAsync(docId)).Should().Equal([5]);
    }

    [Fact]
    public async Task Shadow_store_sees_its_own_rows_and_updates_instead_of_duplicating()
    {
        await using var _ = await IsolatedAsync();
        var docId = NewDocId();

        await Store(1).ApplyChangesAsync(Upsert(docId, 1), [], Ct);
        await StartRebuildAsync();

        // Filtered to v1, the second write would miss the v2 row and insert it again (23505).
        var shadow = Store(2);
        await shadow.ApplyChangesAsync(Upsert(docId, 2), [], Ct);
        await shadow.ApplyChangesAsync(Upsert(docId, 3), [], Ct);

        (await shadow.LoadManyAsync([docId], Ct))[docId].Counter.Should().Be(3);
        (await shadow.ListRecentAsync(1000, Ct)).Should().Contain(e => e.DocumentId == docId);
    }

    [Fact]
    public async Task Clearer_deletes_the_superseded_version_after_promotion()
    {
        await using var _ = await IsolatedAsync();
        var docId = NewDocId();

        await Store(1).ApplyChangesAsync(Upsert(docId, 1), [], Ct);
        await StartRebuildAsync();
        await Store(2).ApplyChangesAsync(Upsert(docId, 2), [], Ct);
        await PromoteAsync();

        await new EfProjectionStateClearer<ActiveCounterEntity, EfTestDbContext>(
            fixture.ContextFactory(), ProcessorId).ClearVersionAsync(1, Ct);

        (await UnfilteredReadAsync(docId)).Should().Equal([2],
            "the sweep must reach the version the filter hides");
    }

    [Fact]
    public async Task Inline_projection_loads_its_own_version_while_another_is_active()
    {
        await using var _ = await IsolatedAsync();
        var docId = NewDocId();

        await StartRebuildAsync();
        await PromoteAsync();

        // A replica whose version cache has not caught up with the promotion yet still writes v1.
        var inline = new DeclaredEfInlineProjection<ActiveCounterEntity, EfTestDbContext>(
            Declaration(), fixture.ContextFactory(), ProjectionVersion.From(() => 1));

        await inline.ProcessAsync([EfTestEnvelopes.ForCounterIncremented(docId, 1, position: 1)], Ct);
        await inline.ProcessAsync([EfTestEnvelopes.ForCounterIncremented(docId, 2, position: 2)], Ct);

        await using var context = await fixture.ContextFactory().CreateDbContextAsync(Ct);
        var v1 = await context.ActiveCounters
            .IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])
            .SingleAsync(e => e.DocumentId == docId && e.RebuildVersion == 1, Ct);
        v1.Counter.Should().Be(3, "the second event must load the v1 row, not insert a duplicate");
    }

    // ------------------------------------------------------------------ helpers

    private static string NewDocId() => $"doc-{Guid.NewGuid():N}";

    private EfStateStore<ActiveCounterEntity, EfTestDbContext> Store(int version)
        => new(fixture.ContextFactory(), ProjectionVersion.From(() => version));

    private static Dictionary<string, ActiveCounterEntity> Upsert(string docId, int counter)
        => new() { [docId] = new ActiveCounterEntity { Counter = counter } };

    private static ProjectionDeclaration<ActiveCounterEntity> Declaration() =>
        DeclareProjection.For<ActiveCounterEntity>(ProcessorId)
            .On<CounterIncremented>(
                id: e => e.EntityId,
                apply: (state, e, _) => new ActiveCounterEntity { Counter = state.Counter + e.Amount })
            .Build();

    private async Task<int[]> PlainReadAsync(string docId)
    {
        await using var context = await fixture.ContextFactory().CreateDbContextAsync(Ct);
        return await context.ActiveCounters
            .Where(e => e.DocumentId == docId)
            .Select(e => e.Counter)
            .ToArrayAsync(Ct);
    }

    private async Task<int[]> UnfilteredReadAsync(string docId)
    {
        await using var context = await fixture.ContextFactory().CreateDbContextAsync(Ct);
        return await context.ActiveCounters
            .IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])
            .Where(e => e.DocumentId == docId)
            .OrderBy(e => e.RebuildVersion)
            .Select(e => e.Counter)
            .ToArrayAsync(Ct);
    }

    private PostgresProjectionRebuildStore RebuildStore() => new(fixture.DataSource);

    private Task StartRebuildAsync()
        => RebuildStore().StartAsync(ProcessorId, ProcessorId, targetPosition: 0, Ct);

    private async Task PromoteAsync()
    {
        IProjectionRebuildCoordinatorStore store = RebuildStore();
        await store.MarkReadyAsync(ProcessorId, Ct);
        await store.CompletePromotionAsync(ProcessorId, cancellationToken: Ct);
    }

    private async Task<IAsyncDisposable> IsolatedAsync()
    {
        await Serial.WaitAsync(Ct);
        await using var conn = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM alberto_projection_rebuild_meta WHERE processor_id = @p";
        cmd.Parameters.AddWithValue("p", ProcessorId);
        await cmd.ExecuteNonQueryAsync(Ct);
        return new Release();
    }

    private sealed class Release : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Serial.Release();
            return ValueTask.CompletedTask;
        }
    }
}
