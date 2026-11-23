using System.Text.Json;
using Alberto.EntityFramework.Inline;
using Alberto.Postgres;
using Alberto.Projections;
using Alberto.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// Inline projections that ran after the append commits, outside the append lock, let two appends
/// to one document project in the reverse of their log order, and the position guard then
/// discarded the earlier event for good.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfInlineProjectionOrderingTests(EfEventStoreFixture fixture)
    : IClassFixture<EfEventStoreFixture>
{
    [Fact]
    public async Task TwoAppendsToOneDocument_WhoseFirstProjectionIsSlow_ProjectBothEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slowFirst = new SlowFirstRun(NewProjection("ordering-slow-first"));
        var store = new EventStore(new PostgresEventStoreBackend(fixture.DataSource), [slowFirst]);
        var entityId = Guid.NewGuid().ToString();

        var appendA = store.AppendAsync([Increment(entityId, 1)], cancellationToken: cancellationToken);
        await slowFirst.FirstRunStarted;
        var appendB = store.AppendAsync([Increment(entityId, 10)], cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);
        slowFirst.Release();
        await Task.WhenAll(appendA, appendB);

        var row = await fixture.ReadEntityAsync(entityId);
        row.Should().NotBeNull();
        row!.Counter.Should().Be(11, "both events must reach the row whichever append projects first");
    }

    [Fact]
    public async Task TwoReplicasOverOneDatabase_ProjectBothEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slowFirst = new SlowFirstRun(NewProjection("ordering-replicas"));
        var replicaA = new EventStore(new PostgresEventStoreBackend(fixture.DataSource), [slowFirst]);
        var replicaB = new EventStore(
            new PostgresEventStoreBackend(fixture.DataSource), [NewProjection("ordering-replicas")]);
        var entityId = Guid.NewGuid().ToString();

        var appendA = replicaA.AppendAsync([Increment(entityId, 1)], cancellationToken: cancellationToken);
        await slowFirst.FirstRunStarted;
        var appendB = replicaB.AppendAsync([Increment(entityId, 10)], cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);
        slowFirst.Release();
        await Task.WhenAll(appendA, appendB);

        var row = await fixture.ReadEntityAsync(entityId);
        row!.Counter.Should().Be(11);
    }

    [Fact]
    public async Task APostAppendHandlerThatAppendsAgain_DoesNotWaitOnTheAppend()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var entityId = Guid.NewGuid().ToString();
        var handler = new AppendsAgain(entityId);
        var store = new EventStore(
            new PostgresEventStoreBackend(fixture.DataSource), [NewProjection("ordering-nested")], [handler]);
        handler.Store = store;

        await store.AppendAsync([Increment(entityId, 1)], cancellationToken: cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

        (await fixture.ReadEntityAsync(entityId))!.Counter.Should().Be(11);
    }

    [Fact]
    public async Task ADcbConflict_LeavesLaterAppendsFree()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new EventStore(
            new PostgresEventStoreBackend(fixture.DataSource), [NewProjection("ordering-conflict")]);
        var entityId = Guid.NewGuid().ToString();
        var tag = new EventTag("counter", entityId);
        EventToPersist Tagged(int amount) => Increment(entityId, amount) with { Tags = [tag] };
        await store.AppendAsync([Tagged(1)], cancellationToken: cancellationToken);

        var conflict = async () => await store.AppendAsync(
            [Tagged(2)], DcbQuery.ByTags(tag.Value), 0, cancellationToken);
        await conflict.Should().ThrowAsync<DcbConflictException>();

        await store.AppendAsync([Tagged(3)], cancellationToken: cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        (await fixture.ReadEntityAsync(entityId))!.Counter.Should().Be(4);
    }

    [Fact]
    public async Task TwoDeclarationsOnOneTable_BothHandlingTheAppend_DoNotWaitOnEachOther()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new EventStore(
            new PostgresEventStoreBackend(fixture.DataSource),
            [NewProjection("ordering-twin-a"), NewProjection("ordering-twin-b")]);
        var entityId = Guid.NewGuid().ToString();

        await store.AppendAsync([Increment(entityId, 1)], cancellationToken: cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

        (await fixture.ReadEntityAsync(entityId))!.Counter.Should().Be(1);
    }

    [Fact]
    public async Task AProjectionThatFails_RollsTheAppendBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = fixture.CreateFactory();
        factory.EnqueueException(new InvalidOperationException("projection failed"));
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            EfProjectionTestFixture.BuildDeclaration("ordering-rollback"), factory);
        var store = new EventStore(new PostgresEventStoreBackend(fixture.DataSource), [projection]);
        var entityId = Guid.NewGuid().ToString();
        var before = await store.ReadAsync(DcbQuery.ByTypes("counter-incremented"), 0, 100_000, cancellationToken);

        var act = async () => await store.AppendAsync([Increment(entityId, 1)], cancellationToken: cancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("projection failed");
        var after = await store.ReadAsync(DcbQuery.ByTypes("counter-incremented"), 0, 100_000, cancellationToken);
        after.Count.Should().Be(before.Count);
        (await fixture.ReadEntityAsync(entityId)).Should().BeNull();
        await store.AppendAsync([Increment(entityId, 2)], cancellationToken: cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        (await fixture.ReadEntityAsync(entityId))!.Counter.Should().Be(2, "the failed append released the append lock");
    }

    [Fact]
    public async Task AProjectionInAnotherDatabase_FailsTheAppendClearly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var elsewhere = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "elsewhere" };
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            EfProjectionTestFixture.BuildDeclaration("ordering-elsewhere"),
            new ControlledDbContextFactory(elsewhere.ConnectionString, retryOnFailure: false));
        var store = new EventStore(new PostgresEventStoreBackend(fixture.DataSource), [projection]);

        var act = async () => await store.AppendAsync(
            [Increment(Guid.NewGuid().ToString(), 1)], cancellationToken: cancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not in the event store's database*");
    }

    [Fact]
    public async Task AProjectionOnARetryingContext_FailsTheAppendClearly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            EfProjectionTestFixture.BuildDeclaration("ordering-retrying"),
            new ControlledDbContextFactory(fixture.ConnectionString, retryOnFailure: true));
        var store = new EventStore(new PostgresEventStoreBackend(fixture.DataSource), [projection]);

        var act = async () => await store.AppendAsync(
            [Increment(Guid.NewGuid().ToString(), 1)], cancellationToken: cancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EnableRetryOnFailure*");
    }

    private DeclaredEfInlineProjection<CounterEntity, EfTestDbContext> NewProjection(string processorId) =>
        new(EfProjectionTestFixture.BuildDeclaration(processorId), fixture.CreateFactory());

    private sealed class AppendsAgain(string entityId) : IPostAppendHandler
    {
        public EventStore? Store { get; set; }

        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "counter-incremented" };

        public async Task ProcessAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken)
        {
            if (events.Any(e => e.EventData.Contains("\"Amount\":10")))
                return;

            await Store!.AppendAsync([Increment(entityId, 10)], cancellationToken: cancellationToken);
        }
    }

    internal static EventToPersist Increment(string entityId, int amount) => new()
    {
        EventType = new EventType("counter-incremented"),
        Tags = [],
        EventData = JsonSerializer.Serialize(new CounterIncremented(entityId, amount)),
    };

    private sealed class SlowFirstRun(DeclaredEfInlineProjection<CounterEntity, EfTestDbContext> inner)
        : ITransactionalInlineProjection
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public Task FirstRunStarted => _started.Task;

        public void Release() => _release.TrySetResult();

        public IReadOnlySet<string> HandledEventTypes => inner.HandledEventTypes;

        public async Task ProcessAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default)
        {
            await StallFirstRunAsync(cancellationToken);
            await inner.ProcessAsync(events, cancellationToken);
        }

        public async Task ProcessInTransactionAsync(
            IReadOnlyList<IEventEnvelope> events, AppendTransaction transaction, CancellationToken cancellationToken = default)
        {
            await StallFirstRunAsync(cancellationToken);
            await inner.ProcessInTransactionAsync(events, transaction, cancellationToken);
        }

        private async Task StallFirstRunAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runs) != 1)
                return;

            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }
}
