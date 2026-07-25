using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Messaging;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Xunit;

namespace Alberto.Dcb.Tests.Regression;

/// <summary>
/// Regression tests pinning edge-case behaviours discovered during or after the main audit
/// implementation. Each region groups tests by the source issue.
///
/// <para>
/// Tests marked <c>[Fact(Skip = "...")]</c> document design gaps or infrastructure-level
/// bugs that cannot be verified without database access or src/ changes. The skip message
/// explains what the missing fix should be.
/// </para>
/// </summary>
public sealed class DiscoveredIssuesTests
{
    // ── Shared helpers ─────────────────────────────────────────────────────────────

    [EventType("discovered-issues-regression-event")]
    private record RegressionEvent(string Label) : IEvent;

    private static readonly string RegressionEventTypeId =
        EventTypeAttribute.GetEventTypeId(typeof(RegressionEvent));

    private static EventToPersist CreateRegressionEvent(string label) =>
        new()
        {
            EventType = new EventType(RegressionEventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(new RegressionEvent(label)),
        };

    /// <summary>
    /// Minimal <see cref="IEventProcessor"/> that delegates each event to a supplied handler.
    /// </summary>
    private sealed class DelegatingProcessor(
        string processorId,
        Func<IEventEnvelope, CancellationToken, Task> handler) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }

        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { RegressionEventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => handler(@event, ct);
    }

    // ── EventTag default(T) null backing fields ────────────────────────────────────
    //
    // EventTag stores its string representation in a private `_value` field that is
    // set only by constructors. For default(EventTag), _value is null.
    // Previously ToString() used string interpolation ($"{Concept}:{Id}") which
    // returned ":". After caching the value at construction time, ToString() now
    // returns null for the default instance.
    //
    // See: src/Alberto.Dcb/EventTag.cs → ToString() / Value property.

    #region EventTag default-struct null fields

    [Fact]
    public void EventTag_Default_ToString_ReturnsNull()
    {
        // Arrange
        var tag = default(EventTag);

        // Act & Assert
        // _value is never set; ToString() returns the null field rather than
        // a formatted string. Callers that assume ToString() is non-null will NPE.
        Assert.Null(tag.ToString());
    }

    [Fact]
    public void EventTag_Default_Value_IsNull()
    {
        var tag = default(EventTag);

        // Value delegates to _value which is null for an uninitialised struct.
        Assert.Null(tag.Value);
    }

    [Fact]
    public void EventTag_Default_ConceptAndId_AreNull()
    {
        var tag = default(EventTag);

        // Both auto-properties backed by unset fields → null.
        // Callers that read Concept or Id without a null-guard will throw
        // downstream (e.g., in LINQ, validation, or dictionary lookups).
        Assert.Null(tag.Concept);
        Assert.Null(tag.Id);
    }

    #endregion

    // ── TagPattern default(T) null backing fields ──────────────────────────────────
    //
    // TagPattern has two cached fields: _value (full string) and _conceptPrefix
    // (used by Matches(string) for wildcard prefix checks). Both are null on
    // default(TagPattern). Additionally, IsWildcard returns true for the default
    // because it is defined as `Id is null` — which is true for an uninitialised
    // struct. This is semantically wrong: a default TagPattern is not a wildcard,
    // but the struct cannot distinguish "wildcard" from "uninitialised".
    //
    // See: src/Alberto.Dcb/TagPattern.cs → ToString() / Matches(string) / IsWildcard.

    #region TagPattern default-struct null fields

    [Fact]
    public void TagPattern_Default_ToString_ReturnsNull()
    {
        var pattern = default(TagPattern);

        // _value is never set; ToString() returns null rather than e.g. ":*".
        Assert.Null(pattern.ToString());
    }

    [Fact]
    public void TagPattern_Default_IsWildcard_IsTrueButSemanticallyWrong()
    {
        var pattern = default(TagPattern);

        // IsWildcard is `Id is null`. For an uninitialised struct, Id is null,
        // so IsWildcard is unexpectedly true. A brand-new default(TagPattern) is
        // NOT a wildcard — it is just uninitialised — but the struct has no way
        // to encode this distinction without a separate boolean sentinel field.
        Assert.True(pattern.IsWildcard);
    }

    [Fact]
    public void TagPattern_Default_MatchesString_ThrowsArgumentNullException()
    {
        var pattern = default(TagPattern);

        // Matches(string) branches on IsWildcard (true for default) and calls
        // tagValue.StartsWith(_conceptPrefix, StringComparison.Ordinal).
        // _conceptPrefix is null, so StartsWith(null, ...) throws ArgumentNullException.
        // Any code path that stores default(TagPattern) in a collection and then
        // calls Matches will fail at runtime rather than at creation time.
        Assert.Throws<ArgumentNullException>(() => pattern.Matches("order:123"));
    }

    [Fact]
    public void TagPattern_Default_MatchesEventTag_ReturnsFalse()
    {
        var pattern = default(TagPattern);
        var tag = new EventTag("order", "123");

        // Matches(EventTag) first checks string.Equals(Concept, tag.Concept, ...).
        // Concept is null for default, so string.Equals(null, "order", ...) is false.
        // The early-return fires before IsWildcard is consulted, so no exception here.
        // This is a safe but surprising edge case: the guard protects by accident,
        // not by design.
        Assert.False(pattern.Matches(tag));
    }

    #endregion

    // ── P1.5: TenantContext hyphen-rejection (breaking change) ─────────────────────
    //
    // The audit tightened the tenant-ID regex from permissive to strict:
    //   ^[a-z][a-z0-9_]{0,62}$
    // This rejects hyphens, uppercase letters, and UUIDs — which were previously
    // accepted. Integrators using hyphenated tenant IDs MUST rename them before
    // upgrading. See upgrade-notes/P1.5.md.
    //
    // Side effect: existing TenancyTests that call SetTenant("tenant-123"),
    // SetTenant("tenant-1"), SetTenant("tenant-2") will now throw ArgumentException.
    // Those tests must be updated to use underscore-delimited IDs (e.g., "tenant_123").
    //
    // See: src/Alberto.Dcb/Tenancy/TenantContext.cs

    #region P1.5 — TenantContext breaking-change coverage

    [Theory]
    [InlineData("tenant-123")]   // hyphen — now rejected
    [InlineData("tenant-1")]     // hyphen — now rejected
    [InlineData("tenant-2")]     // hyphen — now rejected
    [InlineData("Tenant")]       // uppercase — now rejected
    [InlineData("TENANT_A")]     // uppercase + underscore — still rejected
    [InlineData("123tenant")]    // leading digit — now rejected
    public void TenantContext_SetTenant_PreviouslyValidButNowRejected_Throws(string tenantId)
    {
        // Arrange
        var context = new TenantContext();

        // Act & Assert
        // These IDs passed the old permissive check. The new strict regex rejects them.
        // Any existing application data keyed by these IDs requires migration before
        // upgrading to this version of the library.
        Assert.Throws<ArgumentException>(() => context.SetTenant(tenantId));
    }

    [Theory]
    [InlineData("tenant")]          // simple lowercase — always valid
    [InlineData("tenant_123")]      // underscore delimiter — the migration target
    [InlineData("tenant_1")]        // underscore delimiter — the migration target
    [InlineData("tenant_2")]        // underscore delimiter — the migration target
    [InlineData("t")]               // single letter — minimum valid length
    [InlineData("a1b2c3")]          // lowercase alphanumeric — valid
    public void TenantContext_SetTenant_NewAllowlistFormat_Succeeds(string tenantId)
    {
        // Arrange
        var context = new TenantContext();

        // Act
        context.SetTenant(tenantId);

        // Assert: no exception, TenantId was stored
        Assert.Equal(tenantId, context.TenantId);
    }

    #endregion

    // ── RunWorkerAsync: non-shutdown OperationCanceledException handling ────────────
    //
    // RunWorkerAsync has two inner catch clauses:
    //   catch (OperationCanceledException) when (ct.IsCancellationRequested) → break
    //   catch (Exception ex) when (ex is not OperationCanceledException)     → log+continue
    //
    // An OperationCanceledException that is NOT sourced from the worker CT falls
    // through BOTH catches (first requires ct.IsCancellationRequested == true; second
    // excludes OperationCanceledException). It propagates to the outer catch:
    //   catch (OperationCanceledException) { /* shutting down */ }
    // which silently swallows it and terminates the worker.
    //
    // Consequence: the event whose handler threw the non-shutdown OCE is never
    // MarkCompleted'd. Its position stays in-flight. SafeCheckpoint cannot advance
    // past it. On the next flush the checkpoint reflects the position BEFORE the
    // faulting event — the event will be re-processed after restart (at-least-once
    // is preserved) but the non-shutdown OCE is indistinguishable from a graceful
    // shutdown.
    //
    // See: src/Alberto.Dcb/Subscriptions/ControlLoop.cs → RunWorkerAsync (lines ~301–336)

    #region RunWorkerAsync non-shutdown OCE leaves position in-flight

    [Fact]
    public async Task Pipelined_NonShutdownOce_DoesNotCallMarkCompleted_CheckpointStaysBehindFaultingEvent()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        // Three events; InMemory assigns sequential positions starting at 1.
        await backend.AppendAsync(
            [CreateRegressionEvent("fast-1")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 1
        await backend.AppendAsync(
            [CreateRegressionEvent("non-shutdown-oce")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 2
        await backend.AppendAsync(
            [CreateRegressionEvent("fast-3")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 3

        // Gate: fires when all three handlers have been entered (in any order).
        // RunContinuationsAsynchronously prevents inline deadlock if the 3rd
        // increment happens on a thread that also holds a watermark lock.
        var allDispatched = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;

        var processor = new DelegatingProcessor(
            processorId: "non-shutdown-oce-regression",
            handler: (evt, ct) =>
            {
                // Increment first so the count is correct even if we throw below.
                if (Interlocked.Increment(ref dispatchCount) == 3)
                    allDispatched.TrySetResult();

                if (evt.GlobalPosition == 2)
                {
                    // Throw an OperationCanceledException that is NOT sourced from ct.
                    // ct.IsCancellationRequested is false at this point.
                    // This falls through BOTH inner catch clauses and hits the outer one.
                    throw new OperationCanceledException("simulated non-shutdown OCE");
                }

                return Task.CompletedTask;
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));

        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: new ProcessorExecutionOptions(
                BatchingMode: ProcessorBatchingMode.Required,
                MaxConcurrency: 3));

        // ── Act ───────────────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        // Wait until all three handlers have been entered. This is not a wall-clock
        // race: we only proceed once the gate fires.
        await allDispatched.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        // Shut down cleanly.  By this point:
        //   - position 1: handler returned Task.CompletedTask → MarkCompleted(1)
        //   - position 2: handler threw non-shutdown OCE → outer catch swallowed it;
        //                 MarkCompleted(2) was NEVER called → position 2 in-flight
        //   - position 3: handler returned Task.CompletedTask → MarkCompleted(3)
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        // ── Assert ────────────────────────────────────────────────────────────────
        var checkpoint = await checkpoints.GetAsync(
            "non-shutdown-oce-regression",
            TestContext.Current.CancellationToken);

        // SafeCheckpoint = min(in-flight) - 1 = 2 - 1 = 1.
        // The non-shutdown OCE is silently swallowed; the event at position 2 will
        // be re-processed after restart (at-least-once guarantee is preserved).
        //
        // The risk is that callers may throw OperationCanceledException for reasons
        // unrelated to shutdown (e.g., an inner HTTP call timed out), expecting the
        // loop to fault or log. Instead, the loop silently continues with one fewer
        // worker (degraded throughput) and re-queues the event for retry.
        Assert.Equal(1L, checkpoint);
    }

    #endregion

    // ── Outbox claim lifecycle ────────────────────────────────────────────────────

    #region Outbox claim lifecycle

    [Fact]
    public void OutboxStore_ExposesTimeBoundedTokenFencedClaims()
    {
        var claim = typeof(IOutboxStore).GetMethod(nameof(IOutboxStore.ClaimPendingAsync));
        var delivered = typeof(IOutboxStore).GetMethod(nameof(IOutboxStore.MarkDeliveredAsync));
        var failed = typeof(IOutboxStore).GetMethod(nameof(IOutboxStore.MarkFailedAsync));

        Assert.NotNull(claim);
        Assert.Contains(claim.GetParameters(), p => p.ParameterType == typeof(TimeSpan));
        Assert.NotNull(delivered);
        Assert.Equal(typeof(OutboxClaim), delivered.GetParameters()[0].ParameterType);
        Assert.NotNull(failed);
        Assert.Equal(typeof(OutboxClaim), failed.GetParameters()[0].ParameterType);
    }

    #endregion

    // ── EfStateStore.IsDuplicateKeyViolation exception depth ──────────────────────
    //
    // The current implementation uses a one-level check:
    //   ex.InnerException is DbException { SqlState: "23505" }
    //
    // Npgsql wraps the PostgresException (which IS a DbException with SqlState)
    // directly as InnerException of DbUpdateException, so in practice one level
    // is correct for Npgsql. However, other EF providers or future Npgsql versions
    // may wrap the exception more deeply (two or more levels). The original code
    // walked the full InnerException chain, which was more resilient.
    //
    // This cannot be unit-tested here because Alberto.Dcb.Tests does not reference
    // Alberto.Dcb.EntityFramework and should not add that dependency. The check is
    // recorded here for documentation and reported in crossFileNeeds.
    //
    // See: src/Alberto.Dcb.EntityFramework/EfStateStore.cs → IsDuplicateKeyViolation

    #region EfStateStore exception chain depth (infrastructure test only)

    [Fact(Skip =
        "Infrastructure gap: IsDuplicateKeyViolation only checks ex.InnerException " +
        "(one level). A DbException nested two or more levels deep would be missed, " +
        "turning a retryable unique-constraint conflict into an unhandled exception. " +
        "Test requires Alberto.Dcb.EntityFramework project reference and a live " +
        "database provider — add a Testcontainers integration test in a dedicated " +
        "EfStateStore test class within the same project.")]
    public void EfStateStore_IsDuplicateKeyViolation_DoesNotWalkFullInnerExceptionChain()
    {
        // To validate this fix, create a DbUpdateException where the DbException
        // with SqlState "23505" is nested two levels deep:
        //   var pg = new PostgresException(...) { SqlState = "23505" };
        //   var wrapped = new Exception("level-2 wrapper", pg);
        //   var dbu = new DbUpdateException("level-1 wrapper", wrapped);
        //
        // The current one-liner returns false (dbu.InnerException is `wrapped`,
        // not a DbException). The fix should walk the chain:
        //   static bool Walk(Exception? ex) =>
        //     ex is DbException { SqlState: UniqueViolationSqlState } ||
        //     (ex is not null && Walk(ex.InnerException));
        Assert.True(true, "placeholder — see skip reason above");
    }

    #endregion
}
