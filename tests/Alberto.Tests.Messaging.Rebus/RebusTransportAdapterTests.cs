using System.Text;
using System.Text.Json;
using Alberto.Messaging;
using Alberto.Messaging.Postgres;
using Alberto.Postgres;
using Alberto.TestInfrastructure;
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
/// Drives <see cref="RebusTransport"/> through a real <see cref="OutboxRelay"/> against
/// PostgreSQL, proving the recipe in <c>docs/reactors-and-outbox.md</c> works as written.
/// </summary>
/// <remarks>
/// One container hosts both Alberto's outbox table (<c>alberto_outbox_entries</c>) and Rebus's
/// shared queue table (<c>rebus_messages</c>). Each test uses its own queue name so the buses
/// do not consume each other's messages.
/// </remarks>
public sealed class RebusTransportAdapterTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    private NpgsqlDataSource _dataSource = null!;

    public async ValueTask InitializeAsync()
    {
        _container = await ContainerStartup.StartNewAsync(
            () => new PostgreSqlBuilder("postgres:16-alpine").Build());
        PostgresMigrator.Migrate(_container.GetConnectionString(), singleTenant: true);
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        // Either can still be null: InitializeAsync throws out of a failed start or a failed
        // migration, and a NullReferenceException from teardown would hide which.
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    /// <summary>
    /// The adapter as documented: no custom serialization, no routing configuration. Rebus's
    /// default serializer encodes the whole <see cref="AlbertoMessage"/> envelope and the
    /// consumer parses <see cref="AlbertoMessage.Payload"/> itself.
    /// </summary>
    [Fact]
    public async Task DefaultSerializer_DeliversEnvelopeToHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = new TaskCompletionSource<AlbertoMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? correlationId = null;

        using var activator = new BuiltinHandlerActivator();
        activator.Handle<AlbertoMessage>((_, context, msg) =>
        {
            correlationId = context.Headers.GetValueOrDefault("correlation-id");
            received.TrySetResult(msg);
            return Task.CompletedTask;
        });

        using var bus = await StartBusAsync(activator, inputQueue: "orders");

        var entry = PendingEntry(
            "order.placed",
            """{"orderId":"order-42","amount":99.99}""",
            destination: "orders",
            metadata: new Dictionary<string, string> { ["correlation-id"] = "corr-123" });

        await DeliverAsync(new RebusTransport(bus), entry, ct);

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        message.MessageType.Should().Be("order.placed");
        message.Version.Should().Be("1");
        AssertOrderPayload(message.Payload, "order-42");

        // Metadata survives as Rebus headers, forwarded via the optionalHeaders parameter.
        correlationId.Should().Be("corr-123");

        (await ReadOutboxStatusAsync(entry.Id, ct)).Should().Be("delivered");
    }

    /// <summary>
    /// A null <see cref="ExternalMessage.Destination"/> means "use the transport's default
    /// routing". For Rebus that is <c>bus.Send</c>, resolved by the type-based router, which
    /// needs a mapping for the envelope type.
    /// </summary>
    [Fact]
    public async Task NullDestination_FallsBackToTypeBasedRouting()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = new TaskCompletionSource<AlbertoMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var activator = new BuiltinHandlerActivator();
        activator.Handle<AlbertoMessage>((_, _, msg) =>
        {
            received.TrySetResult(msg);
            return Task.CompletedTask;
        });

        using var bus = await StartBusAsync(
            activator, inputQueue: "orders-fallback", typeBasedRoute: "orders-fallback");

        var entry = PendingEntry(
            "order.cancelled",
            """{"orderId":"order-99"}""",
            destination: null);

        await DeliverAsync(new RebusTransport(bus), entry, ct);

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        message.MessageType.Should().Be("order.cancelled");
        AssertOrderPayload(message.Payload, "order-99");

        (await ReadOutboxStatusAsync(entry.Id, ct)).Should().Be("delivered");
    }

    /// <summary>
    /// With <see cref="AlbertoOutboxSerializer"/> registered, the same adapter round-trips
    /// through a serializer that carries Alberto's type and version as headers instead of a
    /// CLR type name.
    /// </summary>
    [Fact]
    public async Task AlbertoSerializer_RoundTripsThroughHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = new TaskCompletionSource<AlbertoMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var activator = new BuiltinHandlerActivator();
        activator.Handle<AlbertoMessage>((_, _, msg) =>
        {
            received.TrySetResult(msg);
            return Task.CompletedTask;
        });

        using var bus = await StartBusAsync(
            activator, inputQueue: "orders-raw", useAlbertoSerializer: true);

        var entry = PendingEntry(
            "order.shipped",
            """{"orderId":"order-7","amount":12.5}""",
            destination: "orders-raw");

        await DeliverAsync(new RebusTransport(bus), entry, ct);

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        message.MessageType.Should().Be("order.shipped");
        message.Version.Should().Be("1");
        AssertOrderPayload(message.Payload, "order-7");
    }

    /// <summary>
    /// What the serializer actually buys: the wire body is the raw domain JSON, not an envelope
    /// wrapping an escaped string.
    /// </summary>
    /// <remarks>
    /// Delivers to a queue no bus is listening on, so the row stays in <c>rebus_messages</c> and
    /// the assertion does not race a consumer.
    /// </remarks>
    [Fact]
    public async Task AlbertoSerializer_WritesRawJsonAsTheWireBody()
    {
        var ct = TestContext.Current.CancellationToken;
        const string unconsumed = "orders-wire-inspect";

        using var activator = new BuiltinHandlerActivator();
        using var bus = await StartBusAsync(
            activator, inputQueue: "orders-wire-sender", useAlbertoSerializer: true);

        var entry = PendingEntry(
            "order.refunded",
            """{"orderId":"order-1","amount":5.25}""",
            destination: unconsumed);

        await DeliverAsync(new RebusTransport(bus), entry, ct);
        (await ReadOutboxStatusAsync(entry.Id, ct)).Should().Be("delivered");

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT body FROM rebus_messages WHERE recipient = @q LIMIT 1", conn);
        cmd.Parameters.AddWithValue("q", unconsumed);

        var body = (byte[])(await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException($"No queued message for '{unconsumed}'."));

        // A double-encoded body would parse as a JSON string; a raw one parses as an object.
        AssertOrderPayload(Encoding.UTF8.GetString(body), "order-1");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<IBus> StartBusAsync(
        BuiltinHandlerActivator activator,
        string inputQueue,
        bool useAlbertoSerializer = false,
        string? typeBasedRoute = null)
    {
        var configurer = Configure.With(activator)
            .Transport(t => t.UsePostgreSql(
                _container.GetConnectionString(),
                tableName: "rebus_messages",
                inputQueueName: inputQueue));

        if (useAlbertoSerializer)
            configurer = configurer.Serialization(
                s => s.Register(_ => (ISerializer)new AlbertoOutboxSerializer()));

        if (typeBasedRoute is not null)
            configurer = configurer.Routing(
                r => r.TypeBased().Map<AlbertoMessage>(typeBasedRoute));

        var bus = configurer.Start();

        // RebusTransport.StartAsync is a no-op, so the bus must be fully up (including its
        // startup table creation) before the relay tries to enqueue.
        await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);
        return bus;
    }

    private static OutboxEntry PendingEntry(
        string messageType,
        string payload,
        string? destination,
        Dictionary<string, string>? metadata = null)
        => new(
            Id: Guid.NewGuid(),
            SourceEventId: Guid.NewGuid(),
            MessageType: messageType,
            Version: "1",
            Payload: payload,
            Metadata: metadata ?? [],
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null,
            Destination: destination);

    /// <summary>
    /// Inserts the entry and runs exactly one relay cycle: claim, publish, mark delivered.
    /// </summary>
    /// <remarks>
    /// In production the row is written by <c>OutboxHandler</c> from a committed event. Seeding
    /// it directly keeps these tests on the transport adapter rather than the control loop.
    /// </remarks>
    private async Task DeliverAsync(IMessageTransport transport, OutboxEntry entry, CancellationToken ct)
    {
        var store = new PostgresOutboxStore(_dataSource);
        await store.InsertAsync(entry, ct);

        var signaling = new PollSignalingOutboxStore(store);
        var relay = new OutboxRelay(signaling, transport, pollingInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await relay.StartAsync(cts.Token);
        await signaling.WhenRelayFinished.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await relay.StopAsync(cts.Token);

        var executeTask = relay.ExecuteTask!;
        try
        {
            await executeTask.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (executeTask.IsCanceled)
        {
            // Normal BackgroundService shutdown: ExecuteTask is cancelled on a clean stop.
        }

        relay.Dispose();
    }

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
    /// Asserts the payload is a JSON object with the expected order id. The jsonb column
    /// normalizes key order and spacing, so compare parsed structure and never strings.
    /// </summary>
    private static void AssertOrderPayload(string payload, string expectedOrderId)
    {
        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
            because: "the payload must stay a JSON object, not become a double-encoded string");
        doc.RootElement.GetProperty("orderId").GetString().Should().Be(expectedOrderId);
    }

    /// <summary>
    /// Completes <see cref="WhenRelayFinished"/> at the start of the second claim, by which
    /// point the first full relay cycle has finished. Avoids sleeping on a guess.
    /// </summary>
    private sealed class PollSignalingOutboxStore(IOutboxStore inner) : IOutboxStore
    {
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pollCount;

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
}
