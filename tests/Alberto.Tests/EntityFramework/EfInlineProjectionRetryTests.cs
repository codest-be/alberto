using Alberto.EntityFramework.Inline;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// TEST-1: Behavioural tests for the EF inline projection retry loop.
/// Covers the normal path, retry-then-succeed, exhaustion (P0.7 recovery signal),
/// non-retryable exception passthrough and idempotency via
/// <see cref="IProjectionEntity.LastProcessedPosition"/>.
///
/// Uses <see cref="EfProjectionTestFixture"/> for a shared Postgres container so the
/// real EF query path (entity load via <c>ToDictionaryAsync</c>) exercises actual SQL.
/// Save behaviour is controlled via <see cref="ControlledDbContextFactory.SaveInterceptor"/>
/// to avoid needing full concurrency setup in the DB.
/// </summary>
public sealed class EfInlineProjectionRetryTests(EfProjectionTestFixture fixture)
    : IClassFixture<EfProjectionTestFixture>
{
    // MaxConcurrencyRetries is 5 in both DeclaredEfInlineProjection and EfInlineProjection.
    private const int MaxRetries = 5;

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

        await projection.ProcessAsync(events, ct: TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        entity.Should().NotBeNull();
        entity!.Counter.Should().Be(7);
        entity.LastProcessedPosition.Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // Retry on DbUpdateConcurrencyException
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_SucceedsAfterConcurrencyConflict_RetriesAndPersists()
    {
        var factory = fixture.CreateFactory();

        // First two save attempts throw; third succeeds.
        factory.EnqueueException(new DbUpdateConcurrencyException("stale xmin"));
        factory.EnqueueException(new DbUpdateConcurrencyException("stale xmin again"));
        factory.EnqueueSuccess();

        var declaration = EfProjectionTestFixture.BuildDeclaration("retry-concurrency-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        var entityId = Guid.NewGuid().ToString();
        var events = new[] { EfTestEnvelopes.ForCounterIncremented(entityId, amount: 3, position: 1) };

        await projection.ProcessAsync(events, ct: TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        entity.Should().NotBeNull("the projection should have committed on the third attempt");
        entity!.Counter.Should().Be(3);
    }

    // -----------------------------------------------------------------------
    // Retry on unique-constraint violation (SqlState 23505)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_SucceedsAfterUniqueViolation_RetriesAndPersists()
    {
        var factory = fixture.CreateFactory();

        // Simulate a concurrent INSERT that lands between our pre-load and SaveChanges.
        var uniqueViolation = new DbUpdateException(
            "insert failed",
            new FakeSqlStateDbException("23505", "duplicate key value"));

        factory.EnqueueException(uniqueViolation);
        factory.EnqueueSuccess();

        var declaration = EfProjectionTestFixture.BuildDeclaration("retry-unique-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        var entityId = Guid.NewGuid().ToString();
        var events = new[] { EfTestEnvelopes.ForCounterIncremented(entityId, amount: 5, position: 1) };

        await projection.ProcessAsync(events, ct: TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        entity.Should().NotBeNull("the projection should have committed on the second attempt");
        entity!.Counter.Should().Be(5);
    }

    // -----------------------------------------------------------------------
    // P0.7 — Exhaustion path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_ExhaustsMaxRetries_ThrowsInlineProjectionExhaustedException()
    {
        var factory = fixture.CreateFactory();

        // Permanent failure: every context's SaveChangesAsync throws.
        var concurrencyEx = new DbUpdateConcurrencyException("always fails");
        factory.PermanentInterceptor = (_, _) => throw concurrencyEx;

        const string processorId = "exhaustion-test";
        var declaration = EfProjectionTestFixture.BuildDeclaration(processorId);
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        var entityId = Guid.NewGuid().ToString();
        var events = new[] { EfTestEnvelopes.ForCounterIncremented(entityId, amount: 1, position: 1) };

        var act = async () => await projection.ProcessAsync(
            events, ct: TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<InlineProjectionExhaustedException>();
        thrown.Which.ProcessorId.Should().Be(processorId);
        thrown.Which.Attempts.Should().Be(MaxRetries);
        thrown.Which.DocumentCount.Should().Be(1);
        thrown.Which.InnerException.Should().BeSameAs(concurrencyEx);
    }

    [Fact]
    public async Task ProcessAsync_ExhaustsMaxRetries_LogsCritical()
    {
        var logSink = new CapturingLogger<DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>>();

        var factory = fixture.CreateFactory();
        factory.PermanentInterceptor = (_, _) => throw new DbUpdateConcurrencyException("always fails");

        var declaration = EfProjectionTestFixture.BuildDeclaration("exhaustion-log-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory, logger: logSink);

        var entityId = Guid.NewGuid().ToString();
        var events = new[] { EfTestEnvelopes.ForCounterIncremented(entityId, amount: 1, position: 1) };

        try
        {
            await projection.ProcessAsync(events, ct: TestContext.Current.CancellationToken);
        }
        catch (InlineProjectionExhaustedException)
        {
            // Expected.
        }

        logSink.CriticalMessages.Should().ContainSingle(
            "a single LogCritical call should fire on exhaustion");
    }

    // -----------------------------------------------------------------------
    // Non-retryable exception passthrough
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_NonRetryableException_PropagatesImmediately()
    {
        var factory = fixture.CreateFactory();

        var invalidOp = new InvalidOperationException("not a concurrency error");
        factory.EnqueueException(invalidOp);

        var declaration = EfProjectionTestFixture.BuildDeclaration("non-retryable-test");
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            declaration, factory);

        var entityId = Guid.NewGuid().ToString();
        var events = new[] { EfTestEnvelopes.ForCounterIncremented(entityId, amount: 1, position: 1) };

        var act = async () => await projection.ProcessAsync(
            events, ct: TestContext.Current.CancellationToken);

        // Should throw the original exception, not InlineProjectionExhaustedException.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("not a concurrency error");

        // Only one factory call should have been made (no retries).
        factory.EnqueueSuccess(); // guard: if a second call were made it would succeed, but it won't be
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
        await projection.ProcessAsync([evt], ct: TestContext.Current.CancellationToken);

        // Second processing of the same event: position 42 <= stored 42, so it is skipped.
        await projection.ProcessAsync([evt], ct: TestContext.Current.CancellationToken);

        var entity = await fixture.ReadEntityAsync(entityId);
        entity!.Counter.Should().Be(10, "the event should have been applied exactly once");
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
        var act = async () => await projection.ProcessAsync(
            [], ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
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

        await projection.ProcessAsync(events, ct: TestContext.Current.CancellationToken);

        var entity1 = await fixture.ReadEntityAsync(id1);
        var entity2 = await fixture.ReadEntityAsync(id2);

        entity1.Should().NotBeNull();
        entity1!.Counter.Should().Be(5, "4 + 1 = 5 after folding both events for id1");

        entity2.Should().NotBeNull();
        entity2!.Counter.Should().Be(9);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Simple <see cref="ILogger{T}"/> that captures messages so tests can assert on log output.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> CriticalMessages { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Critical)
                CriticalMessages.Add(formatter(state, exception));
        }
    }
}
