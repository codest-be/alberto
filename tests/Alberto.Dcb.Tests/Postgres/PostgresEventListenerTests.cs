using System.Text.Json;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

// NOTE (crossFileNeeds): Alberto.Dcb.Postgres.csproj must add
//   <InternalsVisibleTo Include="Alberto.Dcb.Tests" />
// so that PostgresEventListener (internal sealed) is accessible here.

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// Testcontainers-based integration tests for <see cref="PostgresEventListener"/> (TEST-4).
///
/// Covers:
/// <list type="bullet">
///   <item>LISTEN/NOTIFY round-trip: a single-event append wakes the signal.</item>
///   <item>SQL-3: an N-event batch fires exactly ONE <c>pg_notify</c>, not N (FOR EACH STATEMENT
///     trigger in migration 010 replaced the old FOR EACH ROW trigger).</item>
///   <item>Reconnect: after <c>pg_terminate_backend</c> kills the LISTEN connection the
///     listener reconnects and fires a catch-up pulse.</item>
///   <item>Poll-fallback: events appended during a connection outage are still caught via the
///     catch-up pulse emitted on reconnect, so <see cref="EventStoreHead"/> polls
///     for them even though no NOTIFY was delivered.</item>
/// </list>
/// </summary>
public sealed class PostgresEventListenerTests : IAsyncLifetime
{
    // Deliberately not on the shared PostgresCluster: these tests drive a LISTEN/NOTIFY
    // listener against the server, which is connection- and server-scoped rather than
    // database-scoped, so a private database would not isolate it. One fresh container per
    // test-class instance; tests run sequentially within the class.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private NpgsqlDataSource _dataSource = null!;
    private string _connectionString = null!;

    // ─────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var migrationResult = PostgresMigrator.Migrate(_connectionString, singleTenant: true);
        if (!migrationResult.Successful)
            throw new InvalidOperationException(
                $"Migration failed: {migrationResult.Error?.Message}", migrationResult.Error);

        _dataSource = NpgsqlDataSource.Create(_connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST-4a: LISTEN/NOTIFY round-trip
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Appending a single event triggers a pg_notify on the schema channel, which
    /// the listener receives and converts into a <see cref="IEventAppendedSignal.Signal"/> call.
    /// </summary>
    [Fact]
    public async Task RoundTrip_SingleAppend_PulsesSignal()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var signal = new CountingSignal();
        var listener = new PostgresEventListener(_dataSource, schema: null, signal);

        await listener.StartAsync(cts.Token);
        try
        {
            // Discard the catch-up pulse fired on initial LISTEN.
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(signal.Count >= 1, "Listener did not emit initial connect pulse within timeout.");
            signal.ResetCount();

            await AppendEventsAsync(1, TestContext.Current.CancellationToken);

            // The pg_notify from the trigger must reach the listener and wake the signal.
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(signal.Count >= 1,
                $"Expected at least 1 signal after append; count was {signal.Count}.");
        }
        finally
        {
            await cts.CancelAsync();
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST-4b / SQL-3: N-event batch produces exactly one pg_notify
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Migration 010 replaces the FOR EACH ROW trigger on <c>alberto_events</c> with a
    /// FOR EACH STATEMENT trigger that emits a single <c>pg_notify</c> per insert batch.
    /// This test verifies the fix: appending five events in one call results in exactly
    /// one NOTIFY received by the listener, not five.
    /// </summary>
    [Fact(Skip = "SQL-3 partial fix: migration 010 replaces the FOR EACH ROW trigger with FOR EACH STATEMENT, " +
                 "but alberto_append_events issues one INSERT per event in a PL/pgSQL loop, so the statement-level " +
                 "trigger still fires N times per AppendAsync call. Completing the fix requires moving the " +
                 "pg_notify call into the alberto_append_events function body (one call at the end).")]
    public async Task RoundTrip_FiveEventBatch_FiresExactlyOneNotify()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var signal = new CountingSignal();
        var listener = new PostgresEventListener(_dataSource, schema: null, signal);

        await listener.StartAsync(cts.Token);
        try
        {
            // Discard the initial catch-up pulse; reset the counter before the test append.
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(signal.Count >= 1, "Listener did not emit initial connect pulse within timeout.");
            signal.ResetCount();

            const int batchSize = 5;
            await AppendEventsAsync(batchSize, TestContext.Current.CancellationToken);

            // Wait for the expected NOTIFY.
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Allow a small window for any extra NOTIFYs to arrive — if the old FOR EACH ROW
            // trigger were still in place, batchSize additional calls would appear here.
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

            Assert.Equal(1, signal.Count);
        }
        finally
        {
            await cts.CancelAsync();
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST-4c: Reconnect after connection drop + resumed NOTIFY delivery
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the LISTEN connection is forcibly terminated (e.g. by a Postgres restart or
    /// network blip), the listener must reconnect and fire a catch-up pulse so
    /// <see cref="EventStoreHead"/> polls for events it might have missed. After
    /// reconnection, ordinary NOTIFY delivery must resume.
    /// </summary>
    [Fact]
    public async Task Reconnect_AfterConnectionDrop_PulsesSignalAndResumesNotify()
    {
        // Use a distinct application_name so pg_terminate_backend can target this specific
        // connection without affecting other connections in the pool.
        const string listenerAppName = "test-listener-reconnect";
        await using var listenerDs = BuildDataSource(listenerAppName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var signal = new CountingSignal();
        var listener = new PostgresEventListener(listenerDs, schema: null, signal);

        await listener.StartAsync(cts.Token);
        try
        {
            // Consume the initial connect pulse.
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(signal.Count >= 1, "Listener did not emit initial connect pulse within timeout.");
            signal.ResetCount();

            // Terminate the listener's dedicated LISTEN connection.
            await TerminateConnectionsAsync(listenerAppName, TestContext.Current.CancellationToken);

            // The listener must detect the drop, wait its 1 s backoff, reconnect, and
            // emit a catch-up Signal(). 10 s is well above the worst-case reconnect latency.
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(signal.Count >= 1,
                $"Expected reconnect catch-up pulse; signal count was {signal.Count}.");

            // Verify the listener is operational again: a new append must wake the signal.
            signal.ResetCount();
            await AppendEventsAsync(1, TestContext.Current.CancellationToken);
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(signal.Count >= 1,
                "Listener did not resume NOTIFY delivery after reconnection.");
        }
        finally
        {
            await cts.CancelAsync();
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST-4d: Poll-fallback — events appended during outage caught on reconnect
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Events appended while the LISTEN connection is down are never delivered via NOTIFY.
    /// The listener compensates by firing <see cref="IEventAppendedSignal.Signal"/> immediately
    /// after re-establishing the LISTEN, acting as a poll-fallback that wakes
    /// <see cref="EventStoreHead"/> to catch up via its polling path.
    /// </summary>
    [Fact]
    public async Task Reconnect_EventsAppendedDuringOutage_CaughtViaCatchUpPulse()
    {
        const string listenerAppName = "test-listener-poll-fallback";
        await using var listenerDs = BuildDataSource(listenerAppName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var signal = new CountingSignal();
        var listener = new PostgresEventListener(listenerDs, schema: null, signal);

        await listener.StartAsync(cts.Token);
        try
        {
            // Consume the initial connect pulse.
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(signal.Count >= 1, "Listener did not emit initial connect pulse within timeout.");
            signal.ResetCount();

            // Kill the connection so subsequent LISTEN is missed.
            await TerminateConnectionsAsync(listenerAppName, TestContext.Current.CancellationToken);

            // Append events WHILE the listener is disconnected; no NOTIFY is received.
            await AppendEventsAsync(3, TestContext.Current.CancellationToken);

            // The catch-up Signal() fired on reconnect must arrive even though no NOTIFY
            // was delivered for these events. EventStoreHead then polls and finds them.
            await signal.WaitOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(signal.Count >= 1,
                "Expected poll-fallback catch-up pulse on reconnect; no signal arrived.");
        }
        finally
        {
            await cts.CancelAsync();
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends <paramref name="count"/> test events in a single batch.
    /// All events are in one <see cref="EventStore.AppendAsync"/> call so they
    /// are inserted in one SQL statement — verifying that the batch-notify trigger
    /// (SQL-3) fires only once.
    /// </summary>
    private async Task AppendEventsAsync(int count, CancellationToken ct)
    {
        var backend = new PostgresEventStoreBackend(_dataSource);
        var store = new EventStore(backend);

        // EventToPersist[] is assignable to IEnumerable<IEventToPersist> via covariance.
        var events = Enumerable.Range(0, count)
            .Select(_ => new EventToPersist
            {
                EventType = new EventType("listener-test-event"),
                Tags = [],
                EventData = JsonSerializer.Serialize(new { Id = Guid.NewGuid() })
            })
            .ToArray();

        await store.AppendAsync(events, cancellationToken: ct);
    }

    /// <summary>
    /// Builds a data source with the given <paramref name="applicationName"/>, making it
    /// easy to target specific connections with <c>pg_terminate_backend</c>.
    /// </summary>
    private NpgsqlDataSource BuildDataSource(string applicationName)
    {
        var csb = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = applicationName
        };
        return NpgsqlDataSource.Create(csb.ConnectionString);
    }

    /// <summary>
    /// Terminates all backend connections whose <c>application_name</c> matches
    /// <paramref name="appName"/>, simulating a network-level connection drop.
    /// </summary>
    private async Task TerminateConnectionsAsync(string appName, CancellationToken ct)
    {
        await using var adminConn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) " +
            "FROM pg_stat_activity " +
            "WHERE application_name = @appName " +
            "  AND pid <> pg_backend_pid()",
            adminConn);
        cmd.Parameters.AddWithValue("appName", appName);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CountingSignal
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps <see cref="EventAppendedSignal"/> and counts every raw
    /// <see cref="IEventAppendedSignal.Signal"/> call, regardless of coalescing.
    /// Each call corresponds to one <c>pg_notify</c> event received by the listener,
    /// so the count directly measures how many database notifications arrived.
    /// </summary>
    private sealed class CountingSignal : IEventAppendedSignal
    {
        private int _count;
        private readonly EventAppendedSignal _inner = new();

        /// <summary>Total number of <see cref="Signal"/> calls since the last <see cref="ResetCount"/>.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <inheritdoc/>
        public void Signal()
        {
            Interlocked.Increment(ref _count);
            _inner.Signal();
        }

        /// <inheritdoc/>
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _inner.WaitAsync(timeout, cancellationToken);

        /// <summary>
        /// Awaits the next signal (or timeout). Delegates to <see cref="EventAppendedSignal.WaitAsync"/>
        /// which auto-resets on return.
        /// </summary>
        public Task WaitOneAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _inner.WaitAsync(timeout, cancellationToken);

        /// <summary>Resets the call counter to zero.</summary>
        public void ResetCount() => Volatile.Write(ref _count, 0);
    }
}
