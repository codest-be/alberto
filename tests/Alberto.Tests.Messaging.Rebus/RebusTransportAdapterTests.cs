using System.Text;
using System.Text.Json;
using Alberto.Messaging;
using Alberto.Messaging.Postgres;
using Alberto.Postgres;
using FluentAssertions;
using Npgsql;
using Rebus.Activation;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Routing.TypeBased;
using Rebus.Serialization;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Tests.Messaging.Rebus;

/// <summary>
/// End-to-end integration test proving that <see cref="RebusTransport"/> adapts
/// Alberto's <see cref="IMessageTransport"/> seam to deliver outbox entries, via Rebus's
/// PostgreSQL transport, to a receiving Rebus handler.
/// </summary>
/// <remarks>
/// This test class owns its own <see cref="PostgreSqlContainer"/>; it does not share the
/// cluster or template machinery from <c>Alberto.Tests</c>. One container hosts both
/// Alberto's outbox table (<c>alberto_outbox_entries</c>) and Rebus's shared queue table
/// (<c>rebus_messages</c>), so the two table names do not conflict.
/// </remarks>
public sealed class RebusTransportAdapterTests : IAsyncLifetime
{
    // One Postgres container per test class. Container startup cost dominates; the single
    // test method in this class does not warrant the shared-cluster template-clone pattern.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private NpgsqlDataSource _dataSource = null!;

    // -------------------------------------------------------------------------
    // IAsyncLifetime
    // -------------------------------------------------------------------------

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var connStr = _container.GetConnectionString();

        // Run Alberto's single-tenant DbUp migrations so that alberto_outbox_entries and
        // related tables exist before the test inserts into or queries them.
        PostgresMigrator.Migrate(connStr, singleTenant: true);

        _dataSource = NpgsqlDataSource.Create(connStr);
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // PollSignalingOutboxStore: same pattern as OutboxRelayTests.cs
    // -------------------------------------------------------------------------

    /// <summary>
    /// Wraps an <see cref="IOutboxStore"/> and completes <see cref="WhenRelayFinished"/>
    /// at the start of the second <see cref="ClaimPendingAsync"/> call. By that point the
    /// first full relay cycle (claim, publish, mark delivered/failed) is complete.
    /// </summary>
    private sealed class PollSignalingOutboxStore(IOutboxStore inner) : IOutboxStore
    {
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pollCount;

        /// <summary>Completes when the relay begins its second poll, confirming the first batch is done.</summary>
        public Task WhenRelayFinished => _tcs.Task;

        public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
            => inner.InsertAsync(entry, ct);

        public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
            int limit, TimeSpan claimLease, string claimedBy, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _pollCount) >= 2)
                _tcs.TrySetResult();
            return inner.ClaimPendingAsync(limit, claimLease, claimedBy, ct);
        }

        public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default)
            => inner.MarkDeliveredAsync(claim, ct);

        public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default)
            => inner.MarkFailedAsync(claim, error, ct);

        public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default)
            => inner.RetryFailedAsync(messageType, ct);

        public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default)
            => inner.PurgeDeliveredAsync(before, ct);
    }

    // -------------------------------------------------------------------------
    // Test
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OutboxEntry_DeliveredThroughRebusTransport_HandlerReceivesIt()
    {
        // Arrange: capture what the handler receives.
        var handlerTcs = new TaskCompletionSource<AlbertoOutboxPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? capturedCorrelationId = null;

        // Handler MUST be registered before starting the bus.  The BuiltinHandlerActivator
        // activates handlers on demand; registering after bus start risks a message being
        // dispatched before the handler exists, causing Rebus to route it to the error queue.
        using var activator = new BuiltinHandlerActivator();
        activator.Handle<AlbertoOutboxPayload>((bus, context, msg) =>
        {
            capturedCorrelationId = context.Headers.GetValueOrDefault("correlation-id");
            handlerTcs.TrySetResult(msg);
            return Task.CompletedTask;
        });

        var connStr = _container.GetConnectionString();

        // Start the Rebus bus.  UsePostgreSql creates the "rebus_messages" queue table on
        // startup, so the table exists before OutboxRelay calls RebusTransport.PublishAsync.
        // Both the sender path (RebusTransport -> bus.Advanced.Routing.Send("orders", ...))
        // and the receiver path (bus listening on "orders") use the same bus instance,
        // which keeps the test self-contained without a second process or bus instance.
        using var bus = Configure.With(activator)
            .Transport(t => t.UsePostgreSql(
                connStr,
                tableName: "rebus_messages",
                inputQueueName: "orders"))
            .Serialization(s => s.Register(_ => (ISerializer)new AlbertoOutboxSerializer()))
            .Start();

        // Brief pause to let Rebus complete its startup table-creation before the relay
        // attempts to enqueue.  RebusTransport.StartAsync is a no-op, so the bus must be
        // fully up before relay.StartAsync returns.
        await Task.Delay(TimeSpan.FromMilliseconds(400),
            TestContext.Current.CancellationToken);

        var outboxStore = new PostgresOutboxStore(_dataSource);
        var transport = new RebusTransport(bus);

        // The expected payload is already-serialized JSON, exactly what ExternalMessage.Payload
        // looks like coming out of the OutboxHandler.
        const string expectedPayload = """{"orderId":"order-42","amount":99.99}""";
        var entryId = Guid.NewGuid();

        // Insert a pending outbox entry.  In production this row is written by OutboxHandler
        // inside the same transaction as the domain event append; here we seed it directly to
        // keep the test focused on the transport adapter, not the full ControlLoop pipeline.
        var entry = new OutboxEntry(
            Id: entryId,
            SourceEventId: Guid.NewGuid(),
            MessageType: "order.placed",
            Version: "1",
            Payload: expectedPayload,
            Metadata: new Dictionary<string, string> { ["correlation-id"] = "corr-123" },
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null,
            Destination: "orders");

        await outboxStore.InsertAsync(entry, TestContext.Current.CancellationToken);

        // Act: run one relay cycle using the verified PollSignaling pattern.
        var signaling = new PollSignalingOutboxStore(outboxStore);
        var relay = new OutboxRelay(
            signaling,
            transport,
            pollingInterval: TimeSpan.FromMilliseconds(50));

        using var relayCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await relay.StartAsync(relayCts.Token);

        // WhenRelayFinished completes on the second ClaimPendingAsync call, meaning the
        // first cycle (claim -> PublishAsync -> MarkDeliveredAsync) is done.
        await signaling.WhenRelayFinished.WaitAsync(
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await relay.StopAsync(relayCts.Token);
        var executeTask = relay.ExecuteTask!;
        try
        {
            await executeTask.WaitAsync(relayCts.Token);
        }
        catch (OperationCanceledException) when (executeTask.IsCanceled)
        {
            // Normal BackgroundService shutdown: ExecuteTask is cancelled on clean stop.
        }
        relay.Dispose();

        // Assert 1: the Rebus handler received the correct MessageType, Version, and Payload.
        // WaitAsync(10 s) also guards against any Rebus dispatch latency after the relay cycle.
        var received = await handlerTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        received.MessageType.Should().Be("order.placed");
        received.Version.Should().Be("1");

        // Primary double-encoding proof: parse the payload as JSON.
        // If AlbertoOutboxSerializer had re-encoded the payload, the handler would receive
        // a JSON string literal ("{\\"orderId\\":...}") whose root element would be a String,
        // not an Object.  The JSONB column normalizes key order and adds spaces on retrieval,
        // so we compare by parsed value rather than exact string form.
        using var payloadDoc = JsonDocument.Parse(received.Payload);
        payloadDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
            because: "Payload must be a JSON object on the wire, not a double-encoded string");
        payloadDoc.RootElement.GetProperty("orderId").GetString().Should().Be("order-42");
        payloadDoc.RootElement.GetProperty("amount").GetDouble().Should().BeApproximately(99.99, 0.001);

        // Assert 2: ExternalMessage.Metadata entries survive the transport round trip as
        // Rebus headers.  RebusTransport forwards them via the optionalHeaders parameter
        // on bus.Advanced.Routing.Send; the handler reads them back from context.Headers.
        capturedCorrelationId.Should().Be("corr-123");

        // Assert 3: the outbox entry reached 'delivered' status.
        var dbStatus = await ReadOutboxStatusAsync(entryId, TestContext.Current.CancellationToken);
        dbStatus.Should().Be("delivered");

        // Assert 4: direct inspection of the Rebus queue table body column.
        // The handler may have already consumed and deleted the row by now, so this check
        // races with consumption.  A missing row is accepted; the primary double-encoding
        // proof is Assert 1 above.  If a row IS still present its body must parse as the
        // expected JSON object (not a string-wrapped double-encoded value).
        await AssertQueueBodyNotDoubleEncodedIfRowPresent(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies that a null <see cref="ExternalMessage.Destination"/> falls back to Rebus's
    /// type-based router when the bus is configured with a matching type mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ExternalMessage.Destination"/> is documented as optional: null means
    /// "use the transport's default routing for this message type." For
    /// <see cref="RebusTransport"/> this translates to <c>bus.Send(payload)</c>, which
    /// Rebus resolves using its type-based router.
    /// </para>
    /// <para>
    /// The bus configuration MUST include a type mapping for
    /// <see cref="AlbertoOutboxPayload"/>:
    /// <code>
    /// .Routing(r => r.TypeBased().Map&lt;AlbertoOutboxPayload&gt;("target-queue"))
    /// </code>
    /// Without the mapping Rebus throws <see cref="ArgumentException"/>:
    /// "Cannot get destination for message of type AlbertoOutboxPayload because it has
    /// not been mapped! ... .Map&lt;AlbertoOutboxPayload&gt;(someEndpoint)"
    /// </para>
    /// <para>
    /// This test uses a distinct queue name ("orders-fallback") to avoid interference
    /// from the first test's bus still polling the "orders" queue.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OutboxEntry_NullDestination_FallsBackToTypedRouting()
    {
        const string fallbackQueue = "orders-fallback";

        var handlerTcs = new TaskCompletionSource<AlbertoOutboxPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var activator = new BuiltinHandlerActivator();
        activator.Handle<AlbertoOutboxPayload>((_, _, msg) =>
        {
            handlerTcs.TrySetResult(msg);
            return Task.CompletedTask;
        });

        var connStr = _container.GetConnectionString();

        // Configure type-based routing: AlbertoOutboxPayload -> fallbackQueue.
        // This is what allows bus.Send(payload) to resolve the destination without
        // an explicit Destination on the outbox entry.
        using var bus = Configure.With(activator)
            .Transport(t => t.UsePostgreSql(
                connStr,
                tableName: "rebus_messages",
                inputQueueName: fallbackQueue))
            .Serialization(s => s.Register(_ => (ISerializer)new AlbertoOutboxSerializer()))
            .Routing(r => r.TypeBased().Map<AlbertoOutboxPayload>(fallbackQueue))
            .Start();

        await Task.Delay(TimeSpan.FromMilliseconds(400),
            TestContext.Current.CancellationToken);

        var outboxStore = new PostgresOutboxStore(_dataSource);
        var transport = new RebusTransport(bus);

        var entryId = Guid.NewGuid();
        var entry = new OutboxEntry(
            Id: entryId,
            SourceEventId: Guid.NewGuid(),
            MessageType: "order.cancelled",
            Version: "1",
            Payload: """{"orderId":"order-99"}""",
            Metadata: [],
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null,
            Destination: null);   // <-- null: triggers bus.Send fallback path

        await outboxStore.InsertAsync(entry, TestContext.Current.CancellationToken);

        var signaling = new PollSignalingOutboxStore(outboxStore);
        var relay = new OutboxRelay(
            signaling,
            transport,
            pollingInterval: TimeSpan.FromMilliseconds(50));

        using var relayCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await relay.StartAsync(relayCts.Token);
        await signaling.WhenRelayFinished.WaitAsync(
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await relay.StopAsync(relayCts.Token);
        var executeTask = relay.ExecuteTask!;
        try
        {
            await executeTask.WaitAsync(relayCts.Token);
        }
        catch (OperationCanceledException) when (executeTask.IsCanceled)
        {
            // Normal BackgroundService shutdown.
        }
        relay.Dispose();

        // Assert: handler received the message via type-based routing.
        var received = await handlerTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        received.MessageType.Should().Be("order.cancelled");
        received.Version.Should().Be("1");

        using var payloadDoc = JsonDocument.Parse(received.Payload);
        payloadDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
            because: "payload must remain a JSON object through the null-destination path too");
        payloadDoc.RootElement.GetProperty("orderId").GetString().Should().Be("order-99");

        // Assert: outbox entry marked delivered.
        var dbStatus = await ReadOutboxStatusAsync(entryId, TestContext.Current.CancellationToken);
        dbStatus.Should().Be("delivered");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<string> ReadOutboxStatusAsync(Guid entryId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM alberto_outbox_entries WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", entryId);
        return (string)(await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException($"Outbox entry {entryId} not found."));
    }

    /// <summary>
    /// If a row for the "orders" queue still exists in the Rebus messages table, verifies
    /// that its body column contains the raw JSON payload and not a double-encoded string.
    /// Rebus.PostgreSql stores all queues in one shared table keyed by the recipient column.
    /// </summary>
    private async Task AssertQueueBodyNotDoubleEncodedIfRowPresent(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT body FROM rebus_messages WHERE recipient = 'orders' LIMIT 1", conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not byte[] bytes)
            return; // Row already consumed by the handler; primary proof is the handler assertion.

        var bodyText = Encoding.UTF8.GetString(bytes);

        // Parsing as a JSON object proves the body is NOT double-encoded.
        // A double-encoded body would be a JSON string literal whose root ValueKind is String;
        // a correctly serialized payload is a JSON object whose root ValueKind is Object.
        // Note: the JSONB column normalizes key order and spacing on outbox retrieval, so the
        // bytes here reflect the JSONB-normalized form, so compare by parsed structure, not string.
        using var doc = JsonDocument.Parse(bodyText);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
            because: "rebus_messages.body must contain a raw JSON object, not a double-encoded string");
        doc.RootElement.GetProperty("orderId").GetString().Should().Be("order-42");
        doc.RootElement.GetProperty("amount").GetDouble().Should().BeApproximately(99.99, 0.001);
    }
}
