using Alberto.Dcb.EntityFramework.Batching;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alberto.Dcb.Tests.EntityFramework;

/// <summary>
/// TEST-10: Behavioural tests for <see cref="BatchedEfProjection{TDbContext,THandler}"/>.
/// Verifies atomic batch persistence, concurrency-exception handling,
/// multi-batch round-trips, and handler delegation.
/// </summary>
public sealed class BatchedEfProjectionTests(EfProjectionTestFixture fixture)
    : IClassFixture<EfProjectionTestFixture>
{
    // -----------------------------------------------------------------------
    // Handler implementation shared across tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Batch handler that increments the counter for each <see cref="CounterIncremented"/> event.
    /// </summary>
    private sealed class CounterBatchHandler : IEfBatchHandler<EfTestDbContext>
    {
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "counter-incremented" };

        public async Task ApplyAsync(EfTestDbContext context, IEventEnvelope @event, CancellationToken ct)
        {
            if (@event.EventType.Id != "counter-incremented")
                return;

            var payload = System.Text.Json.JsonSerializer.Deserialize<CounterIncremented>(@event.EventData)!;

            // Use FindAsync so EF checks the change tracker before hitting the DB.
            // This makes intra-batch updates for the same entity safe: the first
            // ProcessEventAsync call adds the entity to the tracker and subsequent
            // calls within the same batch see the tracked instance instead of querying
            // (and potentially re-inserting) via the DB.
            //
            // A projection entity is keyed by (DocumentId, RebuildVersion) so a rebuild can
            // write a second copy of the projection alongside the live one. A hand-written
            // batch handler is not version-aware, so it works at the version a projection
            // that has never been rebuilt writes to.
            var existing = await context.Counters.FindAsync(
                [payload.EntityId, ProjectionVersions.Initial], ct);

            if (existing is not null)
            {
                existing.Counter += payload.Amount;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                context.Counters.Add(new CounterEntity
                {
                    DocumentId = payload.EntityId,
                    Counter = payload.Amount,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }
    }

    // -----------------------------------------------------------------------
    // HandledEventTypes
    // -----------------------------------------------------------------------

    [Fact]
    public void HandledEventTypes_DelegatesTo_Handler()
    {
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(
            fixture.CreateFactory());

        projection.HandledEventTypes.Should().ContainSingle()
            .Which.Should().Be("counter-incremented");
    }

    // -----------------------------------------------------------------------
    // ProcessorId defaults to handler type name
    // -----------------------------------------------------------------------

    [Fact]
    public void ProcessorId_Defaults_ToHandlerTypeName()
    {
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(
            fixture.CreateFactory());

        projection.ProcessorId.Should().Be(nameof(CounterBatchHandler));
    }

    [Fact]
    public void ProcessorId_CanBeOverridden_ViaConstructor()
    {
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(
            fixture.CreateFactory(), processorId: "my-custom-id");

        projection.ProcessorId.Should().Be("my-custom-id");
    }

    // -----------------------------------------------------------------------
    // Single-event fallback persists before returning
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessEventAsync_PersistsBeforeReturning()
    {
        var factory = fixture.CreateFactory();
        var entityId = Guid.NewGuid().ToString();
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(factory);

        await projection.ProcessEventAsync(
            EfTestEnvelopes.ForCounterIncremented(entityId, amount: 3, position: 1),
            TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        entity.Should().NotBeNull();
        entity!.Counter.Should().Be(3);
    }

    // -----------------------------------------------------------------------
    // Batch processing commits all changes once
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessBatchAsync_CommitsAccumulatedChanges()
    {
        var factory = fixture.CreateFactory();
        var entityId = Guid.NewGuid().ToString();
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(factory);

        await projection.ProcessBatchAsync(
            [
                EfTestEnvelopes.ForCounterIncremented(entityId, amount: 5, position: 1),
                EfTestEnvelopes.ForCounterIncremented(entityId, amount: 3, position: 2),
            ],
            TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        entity.Should().NotBeNull();
        entity!.Counter.Should().Be(8, "5 + 3 = 8 after one committed batch");
    }

    // -----------------------------------------------------------------------
    // Empty batch is a no-op
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessBatchAsync_WhenEmpty_IsNoOp()
    {
        var factory = fixture.CreateFactory();
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(factory);

        var act = async () => await projection.ProcessBatchAsync(
            [],
            TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    // -----------------------------------------------------------------------
    // Concurrency exception during batch save
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessBatchAsync_DbUpdateConcurrencyException_ThrowsConcurrencyConflictException()
    {
        var factory = fixture.CreateFactory();
        var entityId = Guid.NewGuid().ToString();

        // Inject a concurrency failure on save.
        factory.PermanentInterceptor = (_, _) =>
            throw new DbUpdateConcurrencyException("stale row", []);

        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(factory);

        var act = async () => await projection.ProcessBatchAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 1, position: 1)],
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConcurrencyConflictException>();
    }

    [Fact]
    public async Task ProcessBatchAsync_AfterConcurrencyException_UsesFreshContext()
    {
        var factory = fixture.CreateFactory();
        var entityId = Guid.NewGuid().ToString();

        // Inject one failure, then allow success.
        factory.EnqueueException(new DbUpdateConcurrencyException("stale row", []));
        factory.EnqueueSuccess();

        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(factory);

        try
        {
            await projection.ProcessBatchAsync(
                [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 2, position: 1)],
                TestContext.Current.CancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Expected.
        }

        var act = async () => await projection.ProcessBatchAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 2, position: 2)],
            TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("each batch owns a fresh context");
    }

    // -----------------------------------------------------------------------
    // Multiple process-flush cycles
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MultipleBatches_AccumulateAndFlushCorrectly()
    {
        var factory = fixture.CreateFactory();
        var entityId = Guid.NewGuid().ToString();
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(factory);

        await projection.ProcessBatchAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 10, position: 1)],
            TestContext.Current.CancellationToken);

        await projection.ProcessBatchAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 5, position: 2)],
            TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        entity.Should().NotBeNull();
        entity!.Counter.Should().Be(15, "10 + 5 accumulated across two separate batches");
    }

    // -----------------------------------------------------------------------
    // IsActive / IsRebuilding flags are readable and writable
    // -----------------------------------------------------------------------

    [Fact]
    public void IsActive_DefaultsToTrue()
    {
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(
            fixture.CreateFactory());

        projection.IsActive.Should().BeTrue();
    }

    [Fact]
    public void IsRebuilding_DefaultsToFalse()
    {
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(
            fixture.CreateFactory());

        projection.IsRebuilding.Should().BeFalse();
    }

    [Fact]
    public void IsActive_CanBeSetToFalse()
    {
        var projection = new BatchedEfProjection<EfTestDbContext, CounterBatchHandler>(
            fixture.CreateFactory())
        {
            IsActive = false
        };

        projection.IsActive.Should().BeFalse();
    }
}
