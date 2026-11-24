using System.Collections.Concurrent;
using System.Text.Json;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// <c>React&lt;THandler&gt;</c>: one reactor, several event types, one processor id and one lease.
/// </summary>
public sealed class MultiTypeReactorTests
{
    [EventType("multi-reactor-signed")]
    public sealed record Signed(string DocumentId) : IEvent;

    [EventType("multi-reactor-revised")]
    public sealed record Revised(string DocumentId) : IEvent;

    [EventType("multi-reactor-other")]
    public sealed record Other(string DocumentId) : IEvent;

    public sealed record Call(string Kind, string DocumentId, long Position, DateTime Start, DateTime End, string Replica);

    public sealed class Recorder(string replica)
    {
        public string Replica { get; } = replica;
        public ConcurrentQueue<Call> Calls { get; } = new();
        public ConcurrentQueue<int> HandlerInstances { get; } = new();
        public ConcurrentQueue<string?> Tenants { get; } = new();
        public TimeSpan Work { get; init; } = TimeSpan.Zero;
    }

    private static int _instances;

    public sealed class Handler(Recorder recorder, ITenantAccessor? tenant = null)
    {
        private readonly int _id = Interlocked.Increment(ref _instances);

        private string? TenantOrNull()
        {
            try { return tenant?.TenantId; }
            catch (InvalidOperationException) { return null; }
        }

        public async Task Run(string kind, string documentId, long position, CancellationToken cancellationToken)
        {
            var start = DateTime.UtcNow;
            if (recorder.Work > TimeSpan.Zero)
                await Task.Delay(recorder.Work, cancellationToken);
            recorder.Calls.Enqueue(new Call(kind, documentId, position, start, DateTime.UtcNow, recorder.Replica));
            recorder.HandlerInstances.Enqueue(_id);
            recorder.Tenants.Enqueue(TenantOrNull());
        }
    }

    private static ServiceProvider Build(Recorder recorder, Action<AlbertoModuleBuilder> register)
    {
        var services = new ServiceCollection();
        services.AddTenancy();
        services.AddSingleton(recorder);
        services.AddAlberto("test", builder =>
        {
            builder.WithInMemory();
            register(builder);
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static IEventProcessor ProcessorOf(ServiceProvider provider) =>
        provider.GetKeyedServices<IEventProcessor>("test").Single();

    private static IEventEnvelope Envelope<TEvent>(TEvent @event, long position, string tenantId = "t1")
        where TEvent : IEvent => new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GlobalPosition = position,
            EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(TEvent))),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event),
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow,
        };

    private static AlbertoModuleBuilder TwoTypes(AlbertoModuleBuilder b) => b.React<Handler>("multi", r => r
        .On<Signed>((h, e, ct) => h.Run("signed", e.DocumentId, 0, ct))
        .On<Revised>((h, e, ctx, ct) => h.Run("revised", e.DocumentId, ctx.Position, ct)));

    [Fact]
    public async Task Dispatches_EachEventTypeToItsOwnHandler_AndSkipsOthers()
    {
        var recorder = new Recorder("a");
        using var provider = Build(recorder, b => TwoTypes(b));
        var processor = ProcessorOf(provider);
        var token = TestContext.Current.CancellationToken;

        Assert.Equivalent(new[] { "multi-reactor-signed", "multi-reactor-revised" }, processor.HandledEventTypes, strict: true);
        await processor.ProcessEventAsync(Envelope(new Signed("d1"), 1), token);
        await processor.ProcessEventAsync(Envelope(new Revised("d1"), 2), token);
        await processor.ProcessEventAsync(Envelope(new Other("d1"), 3), token);

        Assert.Equal([("signed", "d1"), ("revised", "d1")], recorder.Calls.Select(c => (c.Kind, c.DocumentId)));
    }

    [Fact]
    public async Task ContextOverload_ReceivesTheEnvelopeContext()
    {
        var recorder = new Recorder("a");
        using var provider = Build(recorder, b => TwoTypes(b));

        await ProcessorOf(provider).ProcessEventAsync(Envelope(new Revised("d1"), 42), TestContext.Current.CancellationToken);

        Assert.Equal(42, recorder.Calls.Single().Position);
    }

    [Fact]
    public async Task EachEvent_GetsAFreshScopeWithItsTenant()
    {
        var recorder = new Recorder("a");
        using var provider = Build(recorder, b => TwoTypes(b));
        var processor = (IBatchableProcessor)ProcessorOf(provider);

        await processor.ProcessBatchAsync(
            [Envelope(new Signed("d1"), 1, "t1"), Envelope(new Revised("d1"), 2, "t2")],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, recorder.HandlerInstances.Distinct().Count());
        Assert.Equal(["t1", "t2"], recorder.Tenants);
    }

    [Fact]
    public void NoOn_ThrowsAtRegistration()
    {
        var act = () => Build(new Recorder("a"), b => b.React<Handler>("multi", _ => { }));

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Matches("(?is)multi.*no event types", exception.Message);
    }

    [Fact]
    public void SameEventTypeTwice_ThrowsAtRegistration()
    {
        var act = () => Build(new Recorder("a"), b => b.React<Handler>("multi", r => r
            .On<Signed>((h, e, ct) => h.Run("a", e.DocumentId, 0, ct))
            .On<Signed>((h, e, ct) => h.Run("b", e.DocumentId, 0, ct))));

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Matches("(?is)Signed.*already handled", exception.Message);
    }

    [Fact]
    public async Task TwoReplicas_HandleBothTypesInLogOrder_NeverAtTheSameTime()
    {
        var token = TestContext.Current.CancellationToken;
        var backend = new InMemoryEventStoreBackend();
        var leases = new InMemoryProcessorLeaseManager(leaseDuration: TimeSpan.FromSeconds(5));
        var checkpoints = new InMemoryFencedCheckpointStore(leases);
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(10));
        var work = TimeSpan.FromMilliseconds(15);

        var recorders = new[] { new Recorder("a") { Work = work }, new Recorder("b") { Work = work } };
        var providers = recorders.Select(r => Build(r, b => b.React<Handler>("multi", on => on
            .On<Signed>((h, e, ctx, ct) => h.Run("signed", e.DocumentId, ctx.Position, ct))
            .On<Revised>((h, e, ctx, ct) => h.Run("revised", e.DocumentId, ctx.Position, ct))))).ToArray();

        var groups = providers.Select((p, i) => new LeaseAwareControlLoopGroup(
            [new ControlLoop(
                ProcessorOf(p), head, backend, checkpoints,
                pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 3,
                executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Required })],
            leases, "c", $"replica-{i}")).ToArray();

        try
        {
            await head.StartAsync(token);
            foreach (var group in groups)
                await group.StartAsync(token);

            var sent = Enumerable.Range(0, 8)
                .Select(i => i % 2 == 0 ? (object)new Signed("d1") : new Revised("d1"))
                .ToArray();
            await backend.AppendAsync(
                [.. sent.Select(e => new EventToPersist
                {
                    EventType = new EventType(EventTypeAttribute.GetEventTypeId(e.GetType())),
                    Tags = [],
                    EventData = JsonSerializer.Serialize(e, e.GetType()),
                })],
                cancellationToken: token);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (recorders.Sum(r => r.Calls.Count) < sent.Length && DateTime.UtcNow < deadline)
                await Task.Delay(20, token);
        }
        finally
        {
            foreach (var group in groups)
            {
                await group.StopAsync(CancellationToken.None);
                await group.DisposeAsync();
            }
            await head.StopAsync(CancellationToken.None);
            foreach (var provider in providers)
                provider.Dispose();
        }

        var calls = recorders.SelectMany(r => r.Calls).OrderBy(c => c.Start).ToList();
        Assert.Equal(8, calls.Count());
        Assert.Equal(["signed", "revised", "signed", "revised", "signed", "revised", "signed", "revised"], calls.Select(c => c.Kind));
        Assert.Equal(calls.Select(c => c.Position).Order(), calls.Select(c => c.Position));
        // one lease means one handler call at a time
        var items = calls.Zip(calls.Skip(1));
        Assert.NotEmpty(items);
        Assert.All(items, p => Assert.True(p.First.End <= p.Second.Start));
        // only the lease holder handles events
        Assert.Single(calls.Select(c => c.Replica).Distinct());
    }
}
