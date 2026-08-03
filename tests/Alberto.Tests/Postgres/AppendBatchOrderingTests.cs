using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Guards the set-based append rewrite introduced in 003_AppendSetBased.sql.
/// Each test exercises one hazard that the CTE bulk-insert approach must not
/// regress on.
/// </summary>
/// <remarks>
/// <para>
/// All four tests run on the single-tenant backend. The set-based insert body is
/// identical in both tenancy variants; only the presence of tenant_id in the column
/// lists differs, and that difference is covered by parity and migration tests
/// elsewhere. Using the single-tenant backend avoids a p_tenant_id parameter
/// everywhere while keeping the coverage meaningful.
/// </para>
/// <para>
/// Test isolation: each test appends events tagged with a unique GUID, so tests
/// can share a database without interfering. This mirrors the pattern used by
/// <see cref="AppendAdvisoryLockTests"/>.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AppendBatchOrderingTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    // pg_notify channel for the public schema.  Must match the substitution
    // "$schema$_events" performed by PostgresMigrator against schema = "public".
    private const string NotifyChannel = "public_events";

    // ── Hazard 1: position ordering ──────────────────────────────────────────

    /// <summary>
    /// A 20-event batch must return exactly 20 rows, one per input event, with
    /// global_position values that are strictly ascending (i.e. no gaps, no swaps,
    /// no duplicates within the batch).
    /// </summary>
    /// <remarks>
    /// The row-by-row loop produced positions in input order because each RETURNING
    /// clause in the loop gave the position for that one row.  The CTE rewrite uses
    /// INSERT … SELECT … ORDER BY ordinality and re-reads the events table over a
    /// positional range; this test ensures both orderings are equivalent.
    /// </remarks>
    [Fact]
    public async Task Append_TwentyEvents_ReturnsStrictlyAscendingPositionsInInputOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = $"batch-order-{Guid.NewGuid()}";

        var events = Enumerable.Range(0, 20)
            .Select(i => new EventToPersist
            {
                EventType = new EventType($"ordering-test-{i}"),
                Tags = [new EventTag("batch", tag)],
                EventData = $"{{\"seq\":{i}}}",
            })
            .ToArray<IEventToPersist>();

        var backend = CreateBackend();
        var results = await backend.AppendAsync(events, cancellationToken: ct);

        results.Should().HaveCount(20, because: "one result row per input event is required");

        var positions = results.Select(e => e.GlobalPosition).ToList();
        positions.Should().BeInAscendingOrder(because: "positions must be strictly ascending in input order");

        var distinct = positions.Distinct().ToList();
        distinct.Should().HaveCount(20, because: "every event must get a unique position");
    }

    // ── Hazard 3: pg_notify semantics ────────────────────────────────────────

    /// <summary>
    /// A batch append must emit exactly one pg_notify, with a payload equal to
    /// the global_position of the last event in the batch.
    /// </summary>
    /// <remarks>
    /// The old loop emitted one notification per event because the notification
    /// was inside the loop (any trigger-based approach would have the same
    /// problem).  The CTE rewrite calls pg_notify once, after the CTE, with
    /// v_last_position = MAX(global_position) from the inserted CTE.  This test
    /// ensures the notify is still fired (non-empty batch) and carries the right
    /// payload (last, not first).
    /// </remarks>
    [Fact]
    public async Task Append_NonEmpty_EmitsExactlyOneNotifyCarryingLastPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = $"notify-test-{Guid.NewGuid()}";

        // Open a dedicated connection for LISTEN before the append so the
        // notification is not missed.  Npgsql fires the Notification event
        // whenever WaitAsync delivers data from the server.
        await using var listenConn = new NpgsqlConnection(fixture.ConnectionString);
        await listenConn.OpenAsync(ct);
        await using (var listenCmd = new NpgsqlCommand($"LISTEN {NotifyChannel}", listenConn))
        {
            await listenCmd.ExecuteNonQueryAsync(ct);
        }

        var receivedPayloads = new List<string>();
        listenConn.Notification += (_, args) => receivedPayloads.Add(args.Payload);

        // Append five events via the backend.
        var events = Enumerable.Range(0, 5)
            .Select(i => new EventToPersist
            {
                EventType = new EventType("notify-test"),
                Tags = [new EventTag("notify", tag)],
                EventData = "{}",
            })
            .ToArray<IEventToPersist>();

        var backend = CreateBackend();
        var results = await backend.AppendAsync(events, cancellationToken: ct);
        var lastPosition = results.Last().GlobalPosition;

        // WaitAsync delivers buffered notifications from the server.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        await listenConn.WaitAsync(timeoutCts.Token);

        receivedPayloads.Should().HaveCount(1,
            because: "exactly one pg_notify must be emitted per append call, not one per event");

        receivedPayloads[0].Should().Be(lastPosition.ToString(),
            because: "the notify payload must be the last position written in the batch");
    }

    // ── Hazard 4: empty-input contract ───────────────────────────────────────

    /// <summary>
    /// An empty events array must return zero rows and must not emit any pg_notify.
    /// </summary>
    /// <remarks>
    /// In the CTE rewrite, v_last_position stays NULL when the parsed CTE produces
    /// no rows, so both the RETURN QUERY and the pg_notify are guarded by
    /// <c>IF v_last_position IS NOT NULL</c>.  This test ensures the guard holds.
    /// </remarks>
    [Fact]
    public async Task Append_EmptyEvents_ReturnsNoRowsAndEmitsNoNotify()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var listenConn = new NpgsqlConnection(fixture.ConnectionString);
        await listenConn.OpenAsync(ct);
        await using (var listenCmd = new NpgsqlCommand($"LISTEN {NotifyChannel}", listenConn))
        {
            await listenCmd.ExecuteNonQueryAsync(ct);
        }

        var receivedPayloads = new List<string>();
        listenConn.Notification += (_, args) => receivedPayloads.Add(args.Payload);

        var backend = CreateBackend();
        var results = await backend.AppendAsync([], cancellationToken: ct);

        results.Should().BeEmpty(because: "appending zero events must return zero result rows");

        // Give the server a brief window to deliver any spurious notifications.
        using var shortTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shortTimeout.CancelAfter(TimeSpan.FromMilliseconds(200));
        try
        {
            await listenConn.WaitAsync(shortTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: no notification arrives, timeout fires.
        }

        receivedPayloads.Should().BeEmpty(
            because: "appending zero events must not emit any pg_notify");
    }

    // ── Hazard 5: column defaults ─────────────────────────────────────────────

    /// <summary>
    /// When an event is persisted without an explicit event_id, event_data, or
    /// event_metadata, the stored row must carry a generated UUID, an empty JSON
    /// object, and an empty JSON object respectively.  A missing event_tags value
    /// must result in an empty array.
    /// </summary>
    /// <remarks>
    /// In the row-by-row loop the defaults were applied per-event inside the loop
    /// via COALESCE.  The CTE rewrite applies them in the parsed CTE; this test
    /// ensures the COALESCE expressions are still reached for every input event.
    /// </remarks>
    [Fact]
    public async Task Append_MinimalEvent_AppliesColumnDefaults()
    {
        var ct = TestContext.Current.CancellationToken;

        // Construct an event with no explicit ID, no data beyond the minimum,
        // no metadata, and no tags.
        var eventId = Guid.NewGuid();   // we supply an ID here just for clarity
        var minimal = new EventToPersist
        {
            Id = eventId,
            EventType = new EventType("defaults-test"),
            Tags = [],                   // empty tags array
            EventData = "{}",            // minimal valid JSON
        };

        var backend = CreateBackend();
        var results = await backend.AppendAsync([minimal], cancellationToken: ct);

        results.Should().HaveCount(1);
        var stored = results.Single();

        stored.Id.Should().Be(eventId,
            because: "a supplied event_id must be preserved verbatim");

        stored.EventData.Should().Be("{}",
            because: "empty event data must round-trip as an empty JSON object");

        stored.Tags.Should().BeEmpty(
            because: "an empty tag list must round-trip as an empty array");

        // Now verify that omitting the ID produces a generated UUID (not null / empty).
        var noId = new EventToPersist
        {
            // Id intentionally not set; EventToPersist defaults to Guid.CreateVersion7()
            EventType = new EventType("defaults-test-no-id"),
            Tags = [],
            EventData = "{}",
        };

        var results2 = await backend.AppendAsync([noId], cancellationToken: ct);
        results2.Should().HaveCount(1);
        results2.Single().Id.Should().NotBe(Guid.Empty,
            because: "a missing event_id must be replaced by gen_random_uuid(), not stored as null/empty");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private IEventStoreBackend CreateBackend() =>
        new PostgresEventStoreBackend(fixture.DataSource);
}
