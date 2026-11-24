using Alberto.EntityFramework.Inline;
using Alberto.Projections;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// Behavioural tests for the EF inline projection: the normal path, a failed save propagating
/// so the append aborts, and idempotency via <see cref="IProjectionEntity.LastProcessedPosition"/>.
///
/// Uses <see cref="EfProjectionTestFixture"/> for a shared Postgres container so the
/// real EF query path (entity load via <c>ToDictionaryAsync</c>) exercises actual SQL.
/// Save behaviour is controlled via <see cref="EfTestDbContext.SaveInterceptor"/>
/// to avoid needing full concurrency setup in the DB.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfInlineProjectionWriteTests(EfProjectionTestFixture fixture)
    : IClassFixture<EfProjectionTestFixture>
{
    // -----------------------------------------------------------------------
    // Happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_HappyPath_PersistsEntity()
    {
        var factory = fixture.CreateFactory();
        var declaration = EfProjectionTestFixture.BuildDeclaration("happy-path-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        var entityId = Guid.NewGuid().ToString();
        var events = new[] { EfTestEnvelopes.ForCounterIncremented(entityId, amount: 7, position: 1) };

        await projection.ProcessAsync(events, AppendTransaction.None, TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        Assert.NotNull(entity);
        Assert.Equal(7, entity!.Counter);
        Assert.Equal(1, entity.LastProcessedPosition);
    }

    // -----------------------------------------------------------------------
    // A failing save aborts the append
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_SaveFailure_PropagatesWithoutRetry()
    {
        var factory = fixture.CreateFactory();

        var invalidOp = new InvalidOperationException("not a concurrency error");
        factory.EnqueueException(invalidOp);

        var declaration = EfProjectionTestFixture.BuildDeclaration("non-retryable-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        var entityId = Guid.NewGuid().ToString();
        var events = new[] { EfTestEnvelopes.ForCounterIncremented(entityId, amount: 1, position: 1) };

        var act = async () => await projection.ProcessAsync(events, AppendTransaction.None, TestContext.Current.CancellationToken);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Equal("not a concurrency error", thrown.Message);

        var entity = await fixture.ReadEntityAsync(entityId);
        Assert.Null(entity);
    }

    // -----------------------------------------------------------------------
    // Idempotency guard via LastProcessedPosition
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_ReprocessingSameEvent_IsIdempotent()
    {
        var factory = fixture.CreateFactory();
        var declaration = EfProjectionTestFixture.BuildDeclaration("idempotency-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        var entityId = Guid.NewGuid().ToString();
        var evt = EfTestEnvelopes.ForCounterIncremented(entityId, amount: 10, position: 42);

        // First processing: entity is persisted with Counter=10, LastProcessedPosition=42.
        await projection.ProcessAsync([evt], AppendTransaction.None, TestContext.Current.CancellationToken);

        // Second processing of the same event: position 42 <= stored 42, so it is skipped.
        await projection.ProcessAsync([evt], AppendTransaction.None, TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        Assert.Equal(10, entity!.Counter);
    }

    // -----------------------------------------------------------------------
    // Empty batch short-circuits without any DB round-trips
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_EmptyBatch_DoesNotCreateContext()
    {
        var factory = fixture.CreateFactory();

        // Attach a permanent interceptor that would throw if SaveChangesAsync were called.
        factory.PermanentInterceptor = (_, _) => throw new InvalidOperationException("should not be called");

        var declaration = EfProjectionTestFixture.BuildDeclaration("empty-batch-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        // Empty batch: no events handled by this projection.
        var act = async () => await projection.ProcessAsync([], AppendTransaction.None, TestContext.Current.CancellationToken);

        await act();
    }

    // -----------------------------------------------------------------------
    // Multiple documents in one batch
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_MultipleDocuments_AllPersisted()
    {
        var factory = fixture.CreateFactory();
        var declaration = EfProjectionTestFixture.BuildDeclaration("multi-doc-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();
        var events = new IEventEnvelope[]
        {
            EfTestEnvelopes.ForCounterIncremented(id1, amount: 4, position: 1),
            EfTestEnvelopes.ForCounterIncremented(id2, amount: 9, position: 2),
            EfTestEnvelopes.ForCounterIncremented(id1, amount: 1, position: 3), // fold with first for id1
        };

        await projection.ProcessAsync(events, AppendTransaction.None, TestContext.Current.CancellationToken);

        var entity1 = await fixture.ReadEntityAsync(id1);
        var entity2 = await fixture.ReadEntityAsync(id2);

        Assert.NotNull(entity1);
        Assert.Equal(5, entity1!.Counter);

        Assert.NotNull(entity2);
        Assert.Equal(9, entity2!.Counter);
    }
}
