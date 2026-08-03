using System.Diagnostics;
using System.Text.Json;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

// NOTE (crossFileNeeds): Alberto.Postgres.csproj must add
//   <InternalsVisibleTo Include="Alberto.Tests" />
// so that PostgresEventListener (internal sealed) is accessible here.

namespace Alberto.Tests.Postgres;

/// <summary>
/// Testcontainers-based integration tests for <see cref="PostgresEventListener"/> (TEST-4).
///
/// Covers:
/// <list type="bullet">
///   <item>LISTEN/NOTIFY round-trip: a single-event append wakes the signal.</item>
///   <item>SQL-3: an N-event batch fires exactly ONE <c>pg_notify</c>, not N (migration 025
///     moved the notification into the append functions).</item>
///   <item>Reconnect: after <c>pg_terminate_backend</c> kills the LISTEN connection the
///     listener reconnects and fires a catch-up pulse.</item>
///   <item>Poll-fallback: events appended during a connection outage are still caught via the
///     catch-up pulse emitted on reconnect, so <see cref="EventStoreHead"/> polls
///     for them even though no NOTIFY was delivered.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresEventListenerTests : IAsyncLifetime
{
    // Deliberately not on the shared PostgresCluster: these tests drive a LISTEN/NOTIFY
    // listener against the server, which is connection- and server-scoped rather than
    // database-scoped, so a private database would not isolate it. One fresh container per
    // test-class instance; tests run sequentially within the class.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private NpgsqlDataSource _dataSource = null!;
    private string _connectionString = null!;

    // A pulse that is going to arrive at all arrives in milliseconds, and every wait below
    // returns the moment it lands, so a generous budget costs a healthy run nothing and only
    // a real failure ever spends it. The reconnect legs get more room because they absorb the
    // listener's 1 s backoff plus a fresh physical connection — TCP, auth, startup — to a
    // container competing with the rest of the suite for Docker and CPU. Ten seconds was not
    // reliably enough for that under a full parallel run.
    private static readonly TimeSpan PulseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(45);

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
            await signal.WaitForPulsesAsync(1, PulseTimeout,
                "Listener did not emit its initial connect pulse.", TestContext.Current.CancellationToken);
            signal.Reset();

            await AppendEventsAsync(1, TestContext.Current.CancellationToken);

            // The pg_notify from the append must reach the listener and wake the signal.
            await signal.WaitForPulsesAsync(1, PulseTimeout,
                "The append's NOTIFY never reached the listener.", TestContext.Current.CancellationToken);
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
    /// Migration 025 emits a single <c>pg_notify</c> per append call from the end of
    /// <c>alberto_append_events</c>, replacing the trigger on <c>alberto_events</c> that
    /// fired once per event inserted — statement-level since 010, but the append function
    /// runs one INSERT statement per event, so statement-level still meant once per event.
    /// This test verifies the fix: appending five events in one call results in exactly
    /// one NOTIFY received by the listener, not five.
    /// </summary>
    [Fact]
    public async Task RoundTrip_FiveEventBatch_FiresExactlyOneNotify()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var signal = new CountingSignal();
        var listener = new PostgresEventListener(_dataSource, schema: null, signal);

        await listener.StartAsync(cts.Token);
        try
        {
            // Discard the initial catch-up pulse; reset the counter before the test append.
            await signal.WaitForPulsesAsync(1, PulseTimeout,
                "Listener did not emit its initial connect pulse.", TestContext.Current.CancellationToken);
            signal.Reset();

            const int batchSize = 5;
            await AppendEventsAsync(batchSize, TestContext.Current.CancellationToken);

            // Wait for the expected NOTIFY.
            await signal.WaitForPulsesAsync(1, PulseTimeout,
                "The batch append's NOTIFY never reached the listener.", TestContext.Current.CancellationToken);

            // Wall-clock, deliberately: this waits for any stray NOTIFY messages to arrive
            // over the PostgreSQL wire — a client-side TimeProvider cannot move the database
            // clock or drain the socket. Advancing a fake clock here would read as determinism
            // that is not there.
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
            await signal.WaitForPulsesAsync(1, PulseTimeout,
                "Listener did not emit its initial connect pulse.", TestContext.Current.CancellationToken);
            signal.Reset();

            // Terminate the listener's dedicated LISTEN connection.
            await TerminateConnectionsAsync(listenerAppName, TestContext.Current.CancellationToken);

            // The listener must detect the drop, wait its 1 s backoff, reconnect, and
            // emit a catch-up Signal().
            await signal.WaitForPulsesAsync(1, ReconnectTimeout,
                "Listener did not emit a catch-up pulse after its connection was terminated.",
                TestContext.Current.CancellationToken);

            // Verify the listener is operational again: a new append must wake the signal.
            signal.Reset();
            await AppendEventsAsync(1, TestContext.Current.CancellationToken);
            await signal.WaitForPulsesAsync(1, PulseTimeout,
                "Listener did not resume NOTIFY delivery after reconnecting.",
                TestContext.Current.CancellationToken);
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
            await signal.WaitForPulsesAsync(1, PulseTimeout,
                "Listener did not emit its initial connect pulse.", TestContext.Current.CancellationToken);
            signal.Reset();

            // Kill the connection so subsequent LISTEN is missed.
            await TerminateConnectionsAsync(listenerAppName, TestContext.Current.CancellationToken);

            // Append events WHILE the listener is disconnected; no NOTIFY is received.
            await AppendEventsAsync(3, TestContext.Current.CancellationToken);

            // The catch-up Signal() fired on reconnect must arrive even though no NOTIFY
            // was delivered for these events. EventStoreHead then polls and finds them.
            await signal.WaitForPulsesAsync(1, ReconnectTimeout,
                "Listener did not emit the poll-fallback catch-up pulse on reconnect.",
                TestContext.Current.CancellationToken);
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
}

// ─────────────────────────────────────────────────────────────────────────────
// CountingSignal
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Counts every raw <see cref="IEventAppendedSignal.Signal"/> call, regardless of coalescing,
/// and lets a test wait for that count to reach a threshold. Each call is one pulse the
/// listener emitted — a <c>pg_notify</c> it received, or the catch-up it fires on (re)connect —
/// so the count directly measures what reached the listener.
///
/// It deliberately does <em>not</em> wrap <see cref="EventAppendedSignal"/>. That class latches
/// a signal and clears the latch when a waiter consumes it, which is right for the production
/// waiter and wrong here: the counter and the latch were separate state, so a counter reset
/// left any latched-but-unconsumed pulse behind, and the next wait returned instantly on that
/// stale latch — before the pulse it was actually waiting for. The assertion then failed on a
/// listener that had done nothing wrong. Here the count <em>is</em> the state a wait blocks on,
/// so the two cannot drift, and <see cref="Reset"/> clears both at once.
/// </summary>
internal sealed class CountingSignal : IEventAppendedSignal
{
    private readonly object _gate = new();
    private int _count;
    private TaskCompletionSource _pulsed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Total number of <see cref="Signal"/> calls since the last <see cref="Reset"/>.</summary>
    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <inheritdoc/>
    public void Signal()
    {
        lock (_gate)
        {
            _count++;
            _pulsed.TrySetResult();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing in these tests waits through the interface — the listener only ever calls
    /// <see cref="Signal"/> — so this keeps the interface's own contract, returning silently
    /// on either a pulse or a timeout, rather than the asserting behaviour of
    /// <see cref="WaitForPulsesAsync"/>.
    /// </remarks>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        int next;
        lock (_gate) next = _count + 1;
        await TryWaitForPulsesAsync(next, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Zeroes the count and drops any pulse already latched, together, so the next wait
    /// blocks on pulses that arrive after this call and on nothing else.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _count = 0;
            _pulsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Waits until <paramref name="count"/> pulses have arrived since the last <see cref="Reset"/>,
    /// failing with <paramref name="because"/> plus what was awaited and for how long if they do
    /// not. Running out of time is the failure, reported as such — the caller is never left to
    /// infer it from a counter that a silent timeout happened to leave at zero.
    /// </summary>
    public async Task WaitForPulsesAsync(
        int count, TimeSpan timeout, string because, CancellationToken cancellationToken)
    {
        if (!await TryWaitForPulsesAsync(count, timeout, cancellationToken).ConfigureAwait(false))
            Assert.Fail(
                $"{because} Waited {timeout.TotalSeconds:0.#} s for {count} pulse(s); {Count} arrived.");
    }

    private async Task<bool> TryWaitForPulsesAsync(
        int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();

        while (true)
        {
            Task pulsed;
            lock (_gate)
            {
                if (_count >= count)
                    return true;

                // Swap in a fresh source when the current one has already fired for a pulse
                // that did not reach the target, so the next wait blocks instead of spinning.
                if (_pulsed.Task.IsCompleted)
                    _pulsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                pulsed = _pulsed.Task;
            }

            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(pulsed, Task.Delay(remaining, delayCts.Token)).ConfigureAwait(false);
            await delayCts.CancelAsync().ConfigureAwait(false); // don't leave the timer armed
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CountingSignalTests
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Pins the two properties the listener tests above rest on. Neither held before: the harness
/// wrapped <see cref="EventAppendedSignal"/>, whose latch a counter reset did not clear, and it
/// forwarded that class's silent-on-timeout contract into assertions that could only report the
/// resulting zero counter. Both produced failures on a listener that was behaving correctly, so
/// they are asserted here rather than left to be rediscovered as a flake.
///
/// No container: these exercise the harness, not PostgreSQL.
/// </summary>
public sealed class CountingSignalTests
{
    /// <summary>
    /// A pulse that arrives after a wait has returned, and is then discarded by
    /// <see cref="CountingSignal.Reset"/>, must not satisfy the next wait. Wrapping
    /// <see cref="EventAppendedSignal"/> left exactly that pulse latched, so the next wait
    /// returned at once — before whatever it was waiting for — with the counter at zero.
    /// </summary>
    [Fact]
    public async Task Reset_AfterAnUnconsumedPulse_DoesNotSatisfyTheNextWait()
    {
        var signal = new CountingSignal();

        signal.Signal();
        await signal.WaitForPulsesAsync(1, TimeSpan.FromSeconds(5), "First pulse.", TestContext.Current.CancellationToken);

        // A second pulse lands with no waiter to consume it, and is then discarded.
        signal.Signal();
        signal.Reset();

        // Wall-clock, deliberately: the point is that the wait blocks for its whole budget
        // rather than returning on the discarded pulse, and only elapsed time shows that.
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() =>
            signal.WaitForPulsesAsync(1, TimeSpan.FromMilliseconds(500), "Third pulse.", TestContext.Current.CancellationToken));

        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(400),
            $"Wait returned after {elapsed.ElapsedMilliseconds} ms; it should have blocked for its full budget.");
        Assert.Equal(0, signal.Count);
    }

    /// <summary>
    /// Running out of time fails, and says so. The old harness returned silently on timeout and
    /// left the caller asserting on a counter, which reported "no pulse arrived" for what was
    /// really "the test stopped waiting".
    /// </summary>
    [Fact]
    public async Task WaitForPulses_WhenTheyDoNotArrive_FailsSayingWhatItWaitedFor()
    {
        var signal = new CountingSignal();
        signal.Signal();

        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() =>
            signal.WaitForPulsesAsync(2, TimeSpan.FromMilliseconds(200), "Second pulse never came.", TestContext.Current.CancellationToken));

        Assert.Contains("Second pulse never came.", failure.Message);
        Assert.Contains("2 pulse(s)", failure.Message);
        Assert.Contains("1 arrived", failure.Message);
    }

    /// <summary>A pulse that arrives while a wait is in flight releases it.</summary>
    [Fact]
    public async Task WaitForPulses_IsReleasedByAPulseArrivingMidWait()
    {
        var signal = new CountingSignal();

        var waiting = signal.WaitForPulsesAsync(2, TimeSpan.FromSeconds(5), "Two pulses.", TestContext.Current.CancellationToken);
        signal.Signal();
        signal.Signal();

        await waiting;
        Assert.Equal(2, signal.Count);
    }
}
