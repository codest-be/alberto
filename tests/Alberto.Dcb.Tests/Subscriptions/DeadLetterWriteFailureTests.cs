using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Regression tests for the double-delivery defect that occurs when
/// <see cref="IDeadLetterStore.StoreAsync"/> throws while dead-lettering an exhausted event.
/// <para>
/// Before the fix: a transient DB error from <c>StoreAsync</c> escaped the middleware,
/// the control loop set <c>IsFaulted</c>, the checkpoint stayed at the pre-batch position,
/// and every event that had already committed side-effects was re-delivered on restart —
/// a silent double-delivery with no observable signal.
/// </para>
/// <para>
/// After the fix: <c>StoreAsync</c> is wrapped in a bounded retry. If the write still
/// cannot be made durable, the failure is logged at error level and the middleware returns
/// normally, allowing the checkpoint to advance.
/// </para>
/// <para>
/// Both dispatch paths are covered, and that is the point of the file rather than an
/// afterthought. The guard was written for <see cref="BatchConsumeMiddlewares.RetryAndDeadLetter"/>
/// while <see cref="ConsumeMiddlewares.RetryAndDeadLetter"/> kept the bare <c>await
/// StoreAsync</c> — the same defect, one file away, with a green test sitting next to it. The
/// two now share <c>RetryAndDeadLetterCore.StoreDeadLetterAsync</c>, and asserting the
/// behaviour on both is what keeps a future change from re-splitting them.
/// </para>
/// </summary>
public sealed class DeadLetterWriteFailureTests
{
    private static readonly DateTime EventCreatedAt =
        new(2026, 7, 26, 12, 34, 56, DateTimeKind.Utc);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static BatchConsumeContext MakeContext(
        IReadOnlyList<IEventEnvelope>? envelopes = null,
        CancellationToken ct = default)
    {
        var batch = envelopes ?? [MakeEnvelope(position: 1)];
        return new BatchConsumeContext
        {
            ProcessorId = "test-processor",
            ModuleKey = "test-module",
            Envelopes = batch,
            IsRebuild = false,
            CancellationToken = ct,
        };
    }

    private static ConsumeEventContext MakeSingleContext(
        IEventEnvelope? envelope = null,
        CancellationToken ct = default) =>
        new()
        {
            ProcessorId = "test-processor",
            ModuleKey = "test-module",
            Envelope = envelope ?? MakeEnvelope(position: 1),
            IsRebuild = false,
            CancellationToken = ct,
        };

    private static IEventEnvelope MakeEnvelope(long position) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-a",
            GlobalPosition = position,
            EventType = new EventType("test-event"),
            Tags = [new EventTag("order", "123")],
            EventData = "{}",
            Metadata = new Dictionary<string, string> { ["correlation-id"] = "corr-1" },
            CreatedAt = EventCreatedAt,
        };

    // ── test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Dead-letter store whose <see cref="IDeadLetterStore.StoreAsync"/> always throws.
    /// Records how many times <c>StoreAsync</c> was called so tests can verify the
    /// bounded retry fired the expected number of attempts.
    /// </summary>
    private sealed class ThrowingDeadLetterStore : IDeadLetterStore
    {
        private int _storeCallCount;

        /// <summary>Number of times <see cref="StoreAsync"/> was called.</summary>
        public int StoreCallCount => _storeCallCount;

        public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _storeCallCount);
            throw new InvalidOperationException("Simulated dead-letter store failure");
        }

        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
            string processorId, string? tenantId = null, int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);

        public Task<int> CountAsync(string processorId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task ClearAsync(string processorId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkForRetryAsync(string processorId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> that captures every log record so tests can assert
    /// on level, message text, and structured properties without a full logging framework.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        /// <summary>All entries captured since construction, in order.</summary>
        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries;

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a transient failure in the dead-letter store write does NOT
    /// propagate through the middleware. The middleware must return normally so
    /// the control loop can advance the checkpoint and avoid re-delivering events
    /// that already committed their side-effects.
    /// </summary>
    [Fact]
    public async Task DeadLetterStoreWrite_WhenThrows_MiddlewareReturnsWithoutThrowing()
    {
        // Arrange
        var store = new ThrowingDeadLetterStore();
        var middleware = BatchConsumeMiddlewares.RetryAndDeadLetter(
            new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = true },
            new AlwaysPermanentClassifier(),
            store,
            timeProvider: null,
            logger: null);

        var context = MakeContext(ct: TestContext.Current.CancellationToken);

        // Act — processor always fails so the single-event dead-letter path is reached.
        var act = async () =>
            await MiddlewareRunner.RunAsync(
                context,
                [middleware],
                () => throw new InvalidOperationException("processor failure"));

        // Assert — the middleware must NOT throw despite StoreAsync throwing.
        // If this assertion fails it means a transient dead-letter write error would
        // have escaped to DispatchBatchAsync, preventing the checkpoint from advancing.
        await act.Should().NotThrowAsync(
            because: "a failing dead-letter write must not propagate — that would strand " +
                     "already-processed events and cause silent double-delivery on restart");

        // The event is still counted as dead-lettered even though the store write failed.
        context.DeadLetteredCount.Should().Be(1);
    }

    /// <summary>
    /// Verifies that when the dead-letter store write fails, the failure is surfaced
    /// at <see cref="LogLevel.Error"/> with the event ID and processor ID in the message,
    /// so operators can detect the missing dead-letter entry without re-delivery occurring.
    /// </summary>
    [Fact]
    public async Task DeadLetterStoreWrite_WhenThrows_LogsErrorNamingEventAndProcessor()
    {
        // Arrange
        var store = new ThrowingDeadLetterStore();
        var logger = new CapturingLogger();
        var middleware = BatchConsumeMiddlewares.RetryAndDeadLetter(
            new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = true },
            new AlwaysPermanentClassifier(),
            store,
            timeProvider: null,
            logger: logger);

        var envelopes = new IEventEnvelope[] { MakeEnvelope(position: 42) };
        var context = MakeContext(envelopes, TestContext.Current.CancellationToken);

        // Act
        await MiddlewareRunner.RunAsync(
            context,
            [middleware],
            () => throw new InvalidOperationException("processor failure"));

        // Assert — at least one Error entry was logged for the dead-letter write failure.
        var errorEntries = logger.Entries
            .Where(e => e.Level == LogLevel.Error)
            .ToList();

        errorEntries.Should().NotBeEmpty(
            because: "a failing dead-letter write must be logged at error level");

        var errorEntry = errorEntries[0];

        // The log message must name the event and the processor so operators can act.
        errorEntry.Message.Should().Contain(envelopes[0].Id.ToString(),
            because: "the error log must identify which event was dropped from the dead-letter queue");
        errorEntry.Message.Should().Contain("test-processor",
            because: "the error log must identify which processor dropped the event");

        // The original exception must be attached so the stack trace is visible in log sinks.
        errorEntry.Exception.Should().NotBeNull(
            because: "the original StoreAsync exception must be forwarded so its stack trace is visible");
    }

    /// <summary>
    /// Verifies that the bounded retry loop attempts the store write the expected number
    /// of times before giving up, rather than either giving up immediately or retrying
    /// indefinitely.
    /// </summary>
    [Fact]
    public async Task DeadLetterStoreWrite_WhenAlwaysThrows_AttemptsTheExpectedNumberOfRetries()
    {
        // Arrange
        var store = new ThrowingDeadLetterStore();
        var middleware = BatchConsumeMiddlewares.RetryAndDeadLetter(
            new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = true },
            new AlwaysPermanentClassifier(),
            store,
            timeProvider: null,
            logger: null);

        var context = MakeContext(ct: TestContext.Current.CancellationToken);

        // Act
        await MiddlewareRunner.RunAsync(
            context,
            [middleware],
            () => throw new InvalidOperationException("processor failure"));

        // Assert — 3 attempts (1 initial + 2 retries) before the loop gives up.
        // This constant matches RetryAndDeadLetterCore.DeadLetterWriteMaxAttempts.
        store.StoreCallCount.Should().Be(3,
            because: "the bounded retry must attempt the write exactly 3 times before giving up");
    }

    // ── single-event dispatch ────────────────────────────────────────────────

    /// <summary>
    /// The same protection on the per-event path. This is the one that was missing: the
    /// per-event middleware awaited <c>StoreAsync</c> bare, so a dead-letter store outage
    /// faulted the processor and held the checkpoint — re-delivering every healthy event the
    /// loop had already processed, on every restart, for as long as the outage lasted.
    /// </summary>
    [Fact]
    public async Task SingleEvent_DeadLetterStoreWrite_WhenThrows_MiddlewareReturnsWithoutThrowing()
    {
        var store = new ThrowingDeadLetterStore();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = true },
            new AlwaysPermanentClassifier(),
            store,
            timeProvider: null,
            logger: null);

        var context = MakeSingleContext(ct: TestContext.Current.CancellationToken);

        var act = async () =>
            await MiddlewareRunner.RunAsync(
                context,
                [middleware],
                () => throw new InvalidOperationException("processor failure"));

        await act.Should().NotThrowAsync(
            because: "a failing dead-letter write must not propagate — that would strand " +
                     "already-processed events and cause silent double-delivery on restart");

        // The event is still counted as dead-lettered even though the store write failed.
        context.DeadLettered.Should().BeTrue();
    }

    [Fact]
    public async Task SingleEvent_DeadLetterStoreWrite_WhenThrows_LogsErrorNamingEventAndProcessor()
    {
        var store = new ThrowingDeadLetterStore();
        var logger = new CapturingLogger();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = true },
            new AlwaysPermanentClassifier(),
            store,
            timeProvider: null,
            logger: logger);

        var envelope = MakeEnvelope(position: 42);
        var context = MakeSingleContext(envelope, TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(
            context,
            [middleware],
            () => throw new InvalidOperationException("processor failure"));

        var errorEntries = logger.Entries
            .Where(e => e.Level == LogLevel.Error)
            .ToList();

        errorEntries.Should().NotBeEmpty(
            because: "a failing dead-letter write must be logged at error level");

        var errorEntry = errorEntries[0];

        errorEntry.Message.Should().Contain(envelope.Id.ToString(),
            because: "the error log must identify which event was dropped from the dead-letter queue");
        errorEntry.Message.Should().Contain("test-processor",
            because: "the error log must identify which processor dropped the event");
        errorEntry.Exception.Should().NotBeNull(
            because: "the original StoreAsync exception must be forwarded so its stack trace is visible");
    }

    [Fact]
    public async Task SingleEvent_DeadLetterStoreWrite_WhenAlwaysThrows_AttemptsTheExpectedNumberOfRetries()
    {
        var store = new ThrowingDeadLetterStore();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = true },
            new AlwaysPermanentClassifier(),
            store,
            timeProvider: null,
            logger: null);

        var context = MakeSingleContext(ct: TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(
            context,
            [middleware],
            () => throw new InvalidOperationException("processor failure"));

        store.StoreCallCount.Should().Be(3,
            because: "both dispatch paths share the same bounded retry, so both must attempt " +
                     "the write exactly 3 times before giving up");
    }

    // ── classifier ────────────────────────────────────────────────────────────

    private sealed class AlwaysPermanentClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Permanent;
    }
}
